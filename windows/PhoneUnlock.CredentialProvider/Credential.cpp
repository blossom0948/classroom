#include "Credential.h"

#include "Guids.h"
#include "Helpers.h"
#include "PipeClient.h"
#include <Shlwapi.h>
#include <utility>

void DllAddRef();
void DllRelease();

namespace
{
constexpr wchar_t ReadyStatus[] =
    L"\uD734\uB300\uD3F0\uC5D0\uC11C \uC0DD\uCCB4\uC778\uC2DD\uC73C\uB85C \uC7A0\uAE08 \uD574\uC81C";
}

const CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR PhoneUnlockFieldDescriptors[FieldCount] =
{
    { FieldTileImage, CPFT_TILE_IMAGE, const_cast<PWSTR>(L"Phone Unlock"), CPFG_CREDENTIAL_PROVIDER_LOGO },
    { FieldTitle, CPFT_SMALL_TEXT, const_cast<PWSTR>(L"Phone Unlock"), CPFG_CREDENTIAL_PROVIDER_LABEL },
    { FieldStatus, CPFT_SMALL_TEXT, const_cast<PWSTR>(L"상태"), GUID_NULL },
    { FieldSubmit, CPFT_SUBMIT_BUTTON, const_cast<PWSTR>(L"휴대폰으로 잠금 해제"), GUID_NULL },
};

const CREDENTIAL_PROVIDER_FIELD_STATE PhoneUnlockFieldStates[FieldCount] =
{
    CPFS_DISPLAY_IN_BOTH,
    CPFS_DISPLAY_IN_BOTH,
    CPFS_DISPLAY_IN_SELECTED_TILE,
    CPFS_DISPLAY_IN_SELECTED_TILE,
};

const CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE PhoneUnlockFieldInteractiveStates[FieldCount] =
{
    CPFIS_NONE,
    CPFIS_NONE,
    CPFIS_NONE,
    CPFIS_NONE,
};

PhoneUnlockCredential::PhoneUnlockCredential() : status_(ReadyStatus)
{
    DllAddRef();
}

PhoneUnlockCredential::~PhoneUnlockCredential()
{
    UnAdvise();
    if (!sid_.empty()) SecureZeroMemory(sid_.data(), sid_.size() * sizeof(wchar_t));
    DllRelease();
}

HRESULT PhoneUnlockCredential::Initialize(
    CREDENTIAL_PROVIDER_USAGE_SCENARIO usageScenario,
    PCWSTR sid,
    std::shared_ptr<std::atomic<bool>> proximityUnlockPending)
{
    if (sid == nullptr || *sid == L'\0')
    {
        return E_INVALIDARG;
    }
    usageScenario_ = usageScenario;
    sid_ = sid;
    proximityUnlockPending_ = std::move(proximityUnlockPending);
    return S_OK;
}

HRESULT PhoneUnlockCredential::QueryInterface(REFIID interfaceId, void** object)
{
    if (object == nullptr) return E_INVALIDARG;
    *object = nullptr;
    if (interfaceId == IID_IUnknown || interfaceId == IID_ICredentialProviderCredential
        || interfaceId == IID_ICredentialProviderCredential2)
    {
        *object = static_cast<ICredentialProviderCredential2*>(this);
    }
    else if (interfaceId == IID_ICredentialProviderCredentialWithFieldOptions)
    {
        *object = static_cast<ICredentialProviderCredentialWithFieldOptions*>(this);
    }
    else
    {
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

ULONG PhoneUnlockCredential::AddRef() { return static_cast<ULONG>(InterlockedIncrement(&referenceCount_)); }
ULONG PhoneUnlockCredential::Release()
{
    const long count = InterlockedDecrement(&referenceCount_);
    if (count == 0) delete this;
    return static_cast<ULONG>(count);
}

HRESULT PhoneUnlockCredential::Advise(ICredentialProviderCredentialEvents* events)
{
    if (events_ != nullptr) events_->Release();
    events_ = events;
    if (events_ != nullptr) events_->AddRef();
    return S_OK;
}

HRESULT PhoneUnlockCredential::UnAdvise()
{
    if (events_ != nullptr)
    {
        events_->Release();
        events_ = nullptr;
    }
    return S_OK;
}

HRESULT PhoneUnlockCredential::SetSelected(BOOL* autoLogon)
{
    if (autoLogon == nullptr) return E_INVALIDARG;
    *autoLogon = autoRequestPending_ ? TRUE : FALSE;
    autoRequestPending_ = false;
    SetStatus(*autoLogon ? L"휴대폰으로 지문 요청을 보내는 중…" : ReadyStatus);
    return S_OK;
}

HRESULT PhoneUnlockCredential::SetDeselected()
{
    autoRequestPending_ = true;
    SetStatus(ReadyStatus);
    return S_OK;
}

HRESULT PhoneUnlockCredential::GetFieldState(
    DWORD fieldId,
    CREDENTIAL_PROVIDER_FIELD_STATE* state,
    CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* interactiveState)
{
    if (state == nullptr || interactiveState == nullptr || fieldId >= FieldCount) return E_INVALIDARG;
    *state = PhoneUnlockFieldStates[fieldId];
    *interactiveState = PhoneUnlockFieldInteractiveStates[fieldId];
    return S_OK;
}

HRESULT PhoneUnlockCredential::GetStringValue(DWORD fieldId, PWSTR* value)
{
    if (value == nullptr || fieldId >= FieldCount) return E_INVALIDARG;
    *value = nullptr;
    switch (fieldId)
    {
    case FieldTitle: return SHStrDupW(L"Phone Unlock", value);
    case FieldStatus: return SHStrDupW(status_.c_str(), value);
    case FieldSubmit: return SHStrDupW(L"지문 요청 다시 보내기", value);
    default: return E_INVALIDARG;
    }
}

HRESULT PhoneUnlockCredential::GetBitmapValue(DWORD fieldId, HBITMAP* bitmap)
{
    if (bitmap == nullptr || fieldId != FieldTileImage) return E_INVALIDARG;
    *bitmap = CreatePhoneUnlockBitmap();
    return *bitmap == nullptr ? HRESULT_FROM_WIN32(GetLastError()) : S_OK;
}

HRESULT PhoneUnlockCredential::GetCheckboxValue(DWORD, BOOL*, PWSTR*) { return E_NOTIMPL; }
HRESULT PhoneUnlockCredential::GetComboBoxValueCount(DWORD, DWORD*, DWORD*) { return E_NOTIMPL; }
HRESULT PhoneUnlockCredential::GetComboBoxValueAt(DWORD, DWORD, PWSTR*) { return E_NOTIMPL; }
HRESULT PhoneUnlockCredential::SetStringValue(DWORD, PCWSTR) { return E_NOTIMPL; }
HRESULT PhoneUnlockCredential::SetCheckboxValue(DWORD, BOOL) { return E_NOTIMPL; }
HRESULT PhoneUnlockCredential::SetComboBoxSelectedValue(DWORD, DWORD) { return E_NOTIMPL; }
HRESULT PhoneUnlockCredential::CommandLinkClicked(DWORD) { return E_NOTIMPL; }

HRESULT PhoneUnlockCredential::GetSubmitButtonValue(DWORD fieldId, DWORD* adjacentTo)
{
    if (adjacentTo == nullptr || fieldId != FieldSubmit) return E_INVALIDARG;
    *adjacentTo = FieldStatus;
    return S_OK;
}

HRESULT PhoneUnlockCredential::GetSerialization(
    CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* response,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* serialization,
    PWSTR* optionalStatusText,
    CREDENTIAL_PROVIDER_STATUS_ICON* optionalStatusIcon)
{
    if (response == nullptr || serialization == nullptr || optionalStatusText == nullptr || optionalStatusIcon == nullptr)
    {
        return E_INVALIDARG;
    }
    *response = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
    ZeroMemory(serialization, sizeof(*serialization));
    *optionalStatusText = nullptr;
    *optionalStatusIcon = CPSI_NONE;

    const bool proximityOnly = proximityUnlockPending_ != nullptr
        && proximityUnlockPending_->exchange(false);
    SetStatus(proximityOnly ? L"휴대폰 근접 자동 해제 확인 중…" : L"휴대폰 승인 대기 중…");
    PhoneUnlockCredentialData credential;
    std::wstring error;
    HRESULT result = proximityOnly
        ? RequestProximityApproval(sid_, &credential, &error)
        : RequestPhoneApproval(sid_, &credential, &error);
    if (FAILED(result))
    {
        SetStatus(error.empty() ? L"휴대폰 승인을 완료하지 못했습니다." : error.c_str());
        credential.SecureClear();
        return S_OK;
    }

    std::wstring protectedPassword;
    result = ProtectPassword(credential.password, &protectedPassword);
    if (!credential.password.empty())
    {
        SecureZeroMemory(credential.password.data(), credential.password.size() * sizeof(wchar_t));
    }
    credential.password.clear();
    if (SUCCEEDED(result))
    {
        result = PackInteractiveUnlockLogon(
            credential.domain,
            credential.username,
            protectedPassword,
            usageScenario_,
            &serialization->rgbSerialization,
            &serialization->cbSerialization);
    }
    if (SUCCEEDED(result))
    {
        result = RetrieveNegotiateAuthPackage(&serialization->ulAuthenticationPackage);
    }
    if (SUCCEEDED(result))
    {
        serialization->clsidCredentialProvider = CLSID_PhoneUnlockProvider;
        *response = CPGSR_RETURN_CREDENTIAL_FINISHED;
        SetStatus(L"지문 승인 완료 · Windows 로그인 중…");
    }
    else
    {
        if (serialization->rgbSerialization != nullptr)
        {
            SecureZeroMemory(serialization->rgbSerialization, serialization->cbSerialization);
            CoTaskMemFree(serialization->rgbSerialization);
            serialization->rgbSerialization = nullptr;
            serialization->cbSerialization = 0;
        }
        SetStatus(L"Windows 자격 증명을 만들지 못했습니다.");
    }

    credential.SecureClear();
    if (!protectedPassword.empty()) SecureZeroMemory(protectedPassword.data(), protectedPassword.size() * sizeof(wchar_t));
    return S_OK;
}

HRESULT PhoneUnlockCredential::ReportResult(
    NTSTATUS status,
    NTSTATUS,
    PWSTR* optionalStatusText,
    CREDENTIAL_PROVIDER_STATUS_ICON* optionalStatusIcon)
{
    if (optionalStatusText == nullptr || optionalStatusIcon == nullptr) return E_INVALIDARG;
    *optionalStatusText = nullptr;
    *optionalStatusIcon = CPSI_NONE;
    if (status != 0)
    {
        *optionalStatusIcon = CPSI_ERROR;
        return SHStrDupW(L"Windows가 저장된 계정 자격 증명을 거부했습니다. 설정 앱에서 비밀번호를 다시 저장하세요.", optionalStatusText);
    }
    return S_OK;
}

HRESULT PhoneUnlockCredential::GetUserSid(PWSTR* sid)
{
    if (sid == nullptr || sid_.empty()) return E_INVALIDARG;
    return SHStrDupW(sid_.c_str(), sid);
}

HRESULT PhoneUnlockCredential::GetFieldOptions(
    DWORD fieldId,
    CREDENTIAL_PROVIDER_CREDENTIAL_FIELD_OPTIONS* options)
{
    if (options == nullptr || fieldId >= FieldCount) return E_INVALIDARG;
    *options = CPCFO_NONE;
    return S_OK;
}

void PhoneUnlockCredential::SetStatus(PCWSTR status)
{
    status_ = status == nullptr ? L"" : status;
    if (events_ != nullptr)
    {
        events_->SetFieldString(this, FieldStatus, status_.c_str());
    }
}
