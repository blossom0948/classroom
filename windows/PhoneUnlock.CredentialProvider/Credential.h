#pragma once

#include <windows.h>
#include <credentialprovider.h>
#include <shlguid.h>
#include <string>

enum PhoneUnlockFieldId : DWORD
{
    FieldTileImage = 0,
    FieldTitle,
    FieldStatus,
    FieldSubmit,
    FieldCount
};

extern const CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR PhoneUnlockFieldDescriptors[FieldCount];
extern const CREDENTIAL_PROVIDER_FIELD_STATE PhoneUnlockFieldStates[FieldCount];
extern const CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE PhoneUnlockFieldInteractiveStates[FieldCount];

class PhoneUnlockCredential final :
    public ICredentialProviderCredential2,
    public ICredentialProviderCredentialWithFieldOptions
{
public:
    PhoneUnlockCredential();
    HRESULT Initialize(CREDENTIAL_PROVIDER_USAGE_SCENARIO usageScenario, PCWSTR sid);

    IFACEMETHODIMP QueryInterface(REFIID interfaceId, void** object) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents* events) override;
    IFACEMETHODIMP UnAdvise() override;
    IFACEMETHODIMP SetSelected(BOOL* autoLogon) override;
    IFACEMETHODIMP SetDeselected() override;
    IFACEMETHODIMP GetFieldState(DWORD fieldId, CREDENTIAL_PROVIDER_FIELD_STATE* state,
        CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* interactiveState) override;
    IFACEMETHODIMP GetStringValue(DWORD fieldId, PWSTR* value) override;
    IFACEMETHODIMP GetBitmapValue(DWORD fieldId, HBITMAP* bitmap) override;
    IFACEMETHODIMP GetCheckboxValue(DWORD fieldId, BOOL* checked, PWSTR* label) override;
    IFACEMETHODIMP GetSubmitButtonValue(DWORD fieldId, DWORD* adjacentTo) override;
    IFACEMETHODIMP GetComboBoxValueCount(DWORD fieldId, DWORD* itemCount, DWORD* selectedItem) override;
    IFACEMETHODIMP GetComboBoxValueAt(DWORD fieldId, DWORD item, PWSTR* value) override;
    IFACEMETHODIMP SetStringValue(DWORD fieldId, PCWSTR value) override;
    IFACEMETHODIMP SetCheckboxValue(DWORD fieldId, BOOL checked) override;
    IFACEMETHODIMP SetComboBoxSelectedValue(DWORD fieldId, DWORD selectedItem) override;
    IFACEMETHODIMP CommandLinkClicked(DWORD fieldId) override;
    IFACEMETHODIMP GetSerialization(CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* response,
        CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* serialization,
        PWSTR* optionalStatusText, CREDENTIAL_PROVIDER_STATUS_ICON* optionalStatusIcon) override;
    IFACEMETHODIMP ReportResult(NTSTATUS status, NTSTATUS substatus,
        PWSTR* optionalStatusText, CREDENTIAL_PROVIDER_STATUS_ICON* optionalStatusIcon) override;

    IFACEMETHODIMP GetUserSid(PWSTR* sid) override;
    IFACEMETHODIMP GetFieldOptions(DWORD fieldId,
        CREDENTIAL_PROVIDER_CREDENTIAL_FIELD_OPTIONS* options) override;

private:
    ~PhoneUnlockCredential();
    void SetStatus(PCWSTR status);

    volatile long referenceCount_ = 1;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO usageScenario_ = CPUS_INVALID;
    std::wstring sid_;
    std::wstring status_ = L"휴대폰에서 지문으로 승인하세요";
    ICredentialProviderCredentialEvents* events_ = nullptr;
};
