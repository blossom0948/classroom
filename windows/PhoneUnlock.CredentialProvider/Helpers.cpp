#include "Helpers.h"

#define SECURITY_WIN32
#include <security.h>
#include <wincred.h>
#include <Shlwapi.h>
#include <limits>
#include <vector>

HRESULT CopyFieldDescriptor(
    const CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR& source,
    CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** destination)
{
    if (destination == nullptr)
    {
        return E_INVALIDARG;
    }
    *destination = nullptr;
    auto copy = static_cast<CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*>(
        CoTaskMemAlloc(sizeof(CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR)));
    if (copy == nullptr)
    {
        return E_OUTOFMEMORY;
    }
    *copy = source;
    copy->pszLabel = nullptr;
    const HRESULT result = source.pszLabel == nullptr ? S_OK : SHStrDupW(source.pszLabel, &copy->pszLabel);
    if (FAILED(result))
    {
        CoTaskMemFree(copy);
        return result;
    }
    *destination = copy;
    return S_OK;
}

HRESULT ProtectPassword(const std::wstring& password, std::wstring* protectedPassword)
{
    if (protectedPassword == nullptr)
    {
        return E_INVALIDARG;
    }
    protectedPassword->clear();
    if (password.empty())
    {
        return S_OK;
    }

    std::vector<wchar_t> mutablePassword(password.begin(), password.end());
    mutablePassword.push_back(L'\0');
    DWORD required = 0;
    CredProtectW(FALSE, mutablePassword.data(), static_cast<DWORD>(mutablePassword.size()),
        nullptr, &required, nullptr);
    DWORD error = GetLastError();
    if (error != ERROR_INSUFFICIENT_BUFFER || required == 0)
    {
        SecureZeroMemory(mutablePassword.data(), mutablePassword.size() * sizeof(wchar_t));
        return error == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(error);
    }

    std::vector<wchar_t> output(required);
    if (!CredProtectW(FALSE, mutablePassword.data(), static_cast<DWORD>(mutablePassword.size()),
        output.data(), &required, nullptr))
    {
        error = GetLastError();
        SecureZeroMemory(mutablePassword.data(), mutablePassword.size() * sizeof(wchar_t));
        SecureZeroMemory(output.data(), output.size() * sizeof(wchar_t));
        return HRESULT_FROM_WIN32(error);
    }

    const size_t outputLength = required > 0 && output[required - 1] == L'\0' ? required - 1 : required;
    protectedPassword->assign(output.data(), outputLength);
    SecureZeroMemory(mutablePassword.data(), mutablePassword.size() * sizeof(wchar_t));
    SecureZeroMemory(output.data(), output.size() * sizeof(wchar_t));
    return S_OK;
}

namespace
{
HRESULT InitUnicodeString(const std::wstring& value, UNICODE_STRING* target)
{
    if (target == nullptr || value.size() > std::numeric_limits<USHORT>::max() / sizeof(wchar_t))
    {
        return E_INVALIDARG;
    }
    target->Length = static_cast<USHORT>(value.size() * sizeof(wchar_t));
    target->MaximumLength = target->Length;
    target->Buffer = const_cast<PWSTR>(value.data());
    return S_OK;
}

void CopyPackedString(const UNICODE_STRING& source, BYTE** destination, BYTE* base, UNICODE_STRING* target)
{
    target->Length = source.Length;
    target->MaximumLength = source.Length;
    if (source.Length == 0)
    {
        target->Buffer = nullptr;
        return;
    }
    CopyMemory(*destination, source.Buffer, source.Length);
    target->Buffer = reinterpret_cast<PWSTR>(static_cast<ULONG_PTR>(*destination - base));
    *destination += source.Length;
}

HRESULT NtStatusToHresult(NTSTATUS status)
{
    return HRESULT_FROM_WIN32(LsaNtStatusToWinError(status));
}
}

HRESULT PackInteractiveUnlockLogon(
    const std::wstring& domain,
    const std::wstring& username,
    const std::wstring& protectedPassword,
    CREDENTIAL_PROVIDER_USAGE_SCENARIO usageScenario,
    BYTE** serialized,
    DWORD* serializedSize)
{
    if (serialized == nullptr || serializedSize == nullptr)
    {
        return E_INVALIDARG;
    }
    *serialized = nullptr;
    *serializedSize = 0;

    KERB_INTERACTIVE_UNLOCK_LOGON input{};
    HRESULT result = InitUnicodeString(domain, &input.Logon.LogonDomainName);
    if (SUCCEEDED(result)) result = InitUnicodeString(username, &input.Logon.UserName);
    if (SUCCEEDED(result)) result = InitUnicodeString(protectedPassword, &input.Logon.Password);
    if (FAILED(result)) return result;

    if (usageScenario == CPUS_LOGON)
    {
        input.Logon.MessageType = KerbInteractiveLogon;
    }
    else if (usageScenario == CPUS_UNLOCK_WORKSTATION)
    {
        input.Logon.MessageType = KerbWorkstationUnlockLogon;
    }
    else
    {
        return E_INVALIDARG;
    }

    const size_t total = sizeof(input)
        + input.Logon.LogonDomainName.Length
        + input.Logon.UserName.Length
        + input.Logon.Password.Length;
    if (total > std::numeric_limits<DWORD>::max())
    {
        return HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW);
    }

    auto output = static_cast<KERB_INTERACTIVE_UNLOCK_LOGON*>(CoTaskMemAlloc(total));
    if (output == nullptr)
    {
        return E_OUTOFMEMORY;
    }
    ZeroMemory(output, total);
    output->Logon.MessageType = input.Logon.MessageType;
    BYTE* cursor = reinterpret_cast<BYTE*>(output) + sizeof(*output);
    BYTE* base = reinterpret_cast<BYTE*>(output);
    CopyPackedString(input.Logon.LogonDomainName, &cursor, base, &output->Logon.LogonDomainName);
    CopyPackedString(input.Logon.UserName, &cursor, base, &output->Logon.UserName);
    CopyPackedString(input.Logon.Password, &cursor, base, &output->Logon.Password);

    *serialized = reinterpret_cast<BYTE*>(output);
    *serializedSize = static_cast<DWORD>(total);
    return S_OK;
}

HRESULT RetrieveNegotiateAuthPackage(ULONG* authenticationPackage)
{
    if (authenticationPackage == nullptr)
    {
        return E_INVALIDARG;
    }
    HANDLE lsa = nullptr;
    NTSTATUS status = LsaConnectUntrusted(&lsa);
    if (status != 0)
    {
        return NtStatusToHresult(status);
    }

    char packageName[] = NEGOSSP_NAME_A;
    LSA_STRING name{};
    name.Buffer = packageName;
    name.Length = static_cast<USHORT>(sizeof(packageName) - 1);
    name.MaximumLength = static_cast<USHORT>(sizeof(packageName));
    status = LsaLookupAuthenticationPackage(lsa, &name, authenticationPackage);
    LsaDeregisterLogonProcess(lsa);
    return status == 0 ? S_OK : NtStatusToHresult(status);
}

HBITMAP CreatePhoneUnlockBitmap()
{
    constexpr int size = 72;
    HDC screen = GetDC(nullptr);
    if (screen == nullptr)
    {
        return nullptr;
    }
    HBITMAP bitmap = CreateCompatibleBitmap(screen, size, size);
    HDC memory = CreateCompatibleDC(screen);
    if (bitmap != nullptr && memory != nullptr)
    {
        HGDIOBJ oldBitmap = SelectObject(memory, bitmap);
        HBRUSH background = CreateSolidBrush(RGB(31, 111, 235));
        RECT bounds{ 0, 0, size, size };
        FillRect(memory, &bounds, background);
        DeleteObject(background);

        HPEN pen = CreatePen(PS_SOLID, 5, RGB(255, 255, 255));
        HGDIOBJ oldPen = SelectObject(memory, pen);
        HGDIOBJ oldBrush = SelectObject(memory, GetStockObject(NULL_BRUSH));
        RoundRect(memory, 20, 9, 52, 63, 8, 8);
        Ellipse(memory, 33, 51, 39, 57);
        SelectObject(memory, oldBrush);
        SelectObject(memory, oldPen);
        DeleteObject(pen);
        SelectObject(memory, oldBitmap);
    }
    if (memory != nullptr) DeleteDC(memory);
    else if (bitmap != nullptr)
    {
        DeleteObject(bitmap);
        bitmap = nullptr;
    }
    ReleaseDC(nullptr, screen);
    return bitmap;
}
