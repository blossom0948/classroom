#pragma once

#include <windows.h>
#include <credentialprovider.h>
#include <ntsecapi.h>
#include <string>

HRESULT CopyFieldDescriptor(
    const CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR& source,
    CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** destination);

HRESULT ProtectPassword(const std::wstring& password, std::wstring* protectedPassword);

HRESULT PackInteractiveUnlockLogon(
    const std::wstring& domain,
    const std::wstring& username,
    const std::wstring& protectedPassword,
    CREDENTIAL_PROVIDER_USAGE_SCENARIO usageScenario,
    BYTE** serialized,
    DWORD* serializedSize);

HRESULT RetrieveNegotiateAuthPackage(ULONG* authenticationPackage);

HBITMAP CreatePhoneUnlockBitmap();
