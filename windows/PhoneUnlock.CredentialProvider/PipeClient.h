#pragma once

#include <windows.h>
#include <string>

struct PhoneUnlockCredentialData
{
    std::wstring domain;
    std::wstring username;
    std::wstring password;

    void SecureClear() noexcept;
};

HRESULT RequestPhoneApproval(
    const std::wstring& sid,
    PhoneUnlockCredentialData* credential,
    std::wstring* errorMessage);

HRESULT RequestProximityApproval(
    const std::wstring& sid,
    PhoneUnlockCredentialData* credential,
    std::wstring* errorMessage);
