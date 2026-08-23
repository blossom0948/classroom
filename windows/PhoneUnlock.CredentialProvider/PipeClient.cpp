#include "PipeClient.h"

#include <wincrypt.h>
#include <algorithm>
#include <array>
#include <string_view>
#include <vector>

namespace
{
constexpr wchar_t PipeName[] = LR"(\\.\pipe\PhoneUnlock.Auth)";
constexpr DWORD ConnectTimeoutMs = 5000;
constexpr DWORD ApprovalTimeoutMs = 40000;
constexpr size_t MaxResponseBytes = 64 * 1024;

void Wipe(std::wstring* value) noexcept
{
    if (value != nullptr && !value->empty())
    {
        SecureZeroMemory(value->data(), value->size() * sizeof(wchar_t));
        value->clear();
    }
}

HRESULT RunOverlappedIo(
    HANDLE pipe,
    bool write,
    void* buffer,
    DWORD size,
    DWORD timeoutMs,
    DWORD* transferred)
{
    *transferred = 0;
    OVERLAPPED operation{};
    operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (operation.hEvent == nullptr)
    {
        return HRESULT_FROM_WIN32(GetLastError());
    }

    BOOL started = write
        ? WriteFile(pipe, buffer, size, transferred, &operation)
        : ReadFile(pipe, buffer, size, transferred, &operation);
    HRESULT result = S_OK;
    if (!started)
    {
        const DWORD error = GetLastError();
        if (error != ERROR_IO_PENDING)
        {
            result = HRESULT_FROM_WIN32(error);
        }
        else
        {
            const DWORD wait = WaitForSingleObject(operation.hEvent, timeoutMs);
            if (wait == WAIT_OBJECT_0)
            {
                if (!GetOverlappedResult(pipe, &operation, transferred, FALSE))
                {
                    result = HRESULT_FROM_WIN32(GetLastError());
                }
            }
            else
            {
                CancelIoEx(pipe, &operation);
                WaitForSingleObject(operation.hEvent, INFINITE);
                result = wait == WAIT_TIMEOUT ? HRESULT_FROM_WIN32(ERROR_TIMEOUT) : E_FAIL;
            }
        }
    }

    CloseHandle(operation.hEvent);
    return result;
}

std::vector<std::string_view> Split(const std::string& value, char separator)
{
    std::vector<std::string_view> parts;
    size_t start = 0;
    while (start <= value.size())
    {
        const size_t end = value.find(separator, start);
        parts.emplace_back(value.data() + start, (end == std::string::npos ? value.size() : end) - start);
        if (end == std::string::npos)
        {
            break;
        }
        start = end + 1;
    }
    return parts;
}

bool DecodeBase64(std::string_view encoded, std::vector<BYTE>* decoded)
{
    DWORD size = 0;
    const std::string input(encoded);
    if (!CryptStringToBinaryA(input.c_str(), static_cast<DWORD>(input.size()), CRYPT_STRING_BASE64, nullptr, &size, nullptr, nullptr))
    {
        return false;
    }
    decoded->resize(size);
    return CryptStringToBinaryA(input.c_str(), static_cast<DWORD>(input.size()), CRYPT_STRING_BASE64, decoded->data(), &size, nullptr, nullptr)
        && (decoded->resize(size), true);
}

bool DecodeUtf16(std::string_view encoded, std::wstring* value)
{
    std::vector<BYTE> bytes;
    if (!DecodeBase64(encoded, &bytes) || bytes.size() % sizeof(wchar_t) != 0)
    {
        return false;
    }
    value->clear();
    value->reserve(bytes.size() / 2);
    for (size_t index = 0; index < bytes.size(); index += 2)
    {
        const wchar_t character = static_cast<wchar_t>(bytes[index] | (bytes[index + 1] << 8));
        if (character == L'\0')
        {
            SecureZeroMemory(bytes.data(), bytes.size());
            return false;
        }
        value->push_back(character);
    }
    SecureZeroMemory(bytes.data(), bytes.size());
    return true;
}

std::wstring DecodeUtf8Error(std::string_view encoded)
{
    std::vector<BYTE> bytes;
    if (!DecodeBase64(encoded, &bytes) || bytes.empty())
    {
        return L"Phone Unlock 서비스가 요청을 거부했습니다.";
    }
    const int chars = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
        reinterpret_cast<const char*>(bytes.data()), static_cast<int>(bytes.size()), nullptr, 0);
    if (chars <= 0)
    {
        return L"Phone Unlock 서비스 오류 응답을 읽을 수 없습니다.";
    }
    std::wstring result(static_cast<size_t>(chars), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS,
        reinterpret_cast<const char*>(bytes.data()), static_cast<int>(bytes.size()), result.data(), chars);
    return result;
}
}

void PhoneUnlockCredentialData::SecureClear() noexcept
{
    Wipe(&domain);
    Wipe(&username);
    Wipe(&password);
}

HRESULT RequestPhoneApproval(
    const std::wstring& sid,
    PhoneUnlockCredentialData* credential,
    std::wstring* errorMessage)
{
    if (credential == nullptr || errorMessage == nullptr || sid.empty())
    {
        return E_INVALIDARG;
    }
    credential->SecureClear();
    errorMessage->clear();

    if (!WaitNamedPipeW(PipeName, ConnectTimeoutMs))
    {
        *errorMessage = L"Phone Unlock 서비스에 연결할 수 없습니다.";
        return HRESULT_FROM_WIN32(GetLastError());
    }
    HANDLE pipe = CreateFileW(PipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
        FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
    if (pipe == INVALID_HANDLE_VALUE)
    {
        *errorMessage = L"Phone Unlock 인증 채널을 열 수 없습니다.";
        return HRESULT_FROM_WIN32(GetLastError());
    }

    std::string request = "AUTH|";
    request.reserve(request.size() + sid.size() + 1);
    for (const wchar_t character : sid)
    {
        if (character > 0x7f)
        {
            CloseHandle(pipe);
            return E_INVALIDARG;
        }
        request.push_back(static_cast<char>(character));
    }
    request.push_back('\n');

    DWORD transferred = 0;
    HRESULT result = RunOverlappedIo(pipe, true, request.data(), static_cast<DWORD>(request.size()),
        ConnectTimeoutMs, &transferred);
    if (FAILED(result) || transferred != request.size())
    {
        *errorMessage = L"Phone Unlock 서비스에 요청을 보내지 못했습니다.";
        CloseHandle(pipe);
        return FAILED(result) ? result : E_FAIL;
    }

    std::string response;
    std::array<char, 2048> chunk{};
    const ULONGLONG deadline = GetTickCount64() + ApprovalTimeoutMs;
    while (response.size() < MaxResponseBytes)
    {
        const ULONGLONG now = GetTickCount64();
        if (now >= deadline)
        {
            result = HRESULT_FROM_WIN32(ERROR_TIMEOUT);
            break;
        }
        transferred = 0;
        result = RunOverlappedIo(pipe, false, chunk.data(), static_cast<DWORD>(chunk.size()),
            static_cast<DWORD>(deadline - now), &transferred);
        if (FAILED(result) || transferred == 0)
        {
            break;
        }
        response.append(chunk.data(), transferred);
        const size_t newline = response.find('\n');
        if (newline != std::string::npos)
        {
            response.resize(newline);
            result = S_OK;
            break;
        }
    }
    CloseHandle(pipe);

    if (FAILED(result) || response.empty() || response.size() >= MaxResponseBytes)
    {
        *errorMessage = result == HRESULT_FROM_WIN32(ERROR_TIMEOUT)
            ? L"휴대폰 승인 시간이 초과되었습니다."
            : L"Phone Unlock 서비스 응답을 받지 못했습니다.";
        return FAILED(result) ? result : E_FAIL;
    }

    const auto parts = Split(response, '|');
    if (parts.size() == 4 && parts[0] == "SUCCESS")
    {
        if (!DecodeUtf16(parts[1], &credential->domain)
            || !DecodeUtf16(parts[2], &credential->username)
            || !DecodeUtf16(parts[3], &credential->password)
            || credential->username.empty())
        {
            credential->SecureClear();
            *errorMessage = L"서비스 자격 증명 응답이 손상되었습니다.";
            return E_FAIL;
        }
        return S_OK;
    }
    if (parts.size() == 3 && parts[0] == "ERROR")
    {
        *errorMessage = DecodeUtf8Error(parts[2]);
        return E_ACCESSDENIED;
    }

    *errorMessage = L"알 수 없는 Phone Unlock 서비스 응답입니다.";
    return E_FAIL;
}
