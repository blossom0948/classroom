#pragma once

#include <windows.h>
#include <credentialprovider.h>
#include <atomic>
#include <memory>
#include <mutex>
#include <thread>
#include <vector>

class PhoneUnlockCredential;

class PhoneUnlockProvider final : public ICredentialProvider, public ICredentialProviderSetUserArray
{
public:
    PhoneUnlockProvider();

    IFACEMETHODIMP QueryInterface(REFIID interfaceId, void** object) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;

    IFACEMETHODIMP SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO usageScenario, DWORD flags) override;
    IFACEMETHODIMP SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* serialization) override;
    IFACEMETHODIMP Advise(ICredentialProviderEvents* events, UINT_PTR adviseContext) override;
    IFACEMETHODIMP UnAdvise() override;
    IFACEMETHODIMP GetFieldDescriptorCount(DWORD* count) override;
    IFACEMETHODIMP GetFieldDescriptorAt(DWORD index, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** descriptor) override;
    IFACEMETHODIMP GetCredentialCount(DWORD* count, DWORD* defaultCredential, BOOL* autoLogonWithDefault) override;
    IFACEMETHODIMP GetCredentialAt(DWORD index, ICredentialProviderCredential** credential) override;

    IFACEMETHODIMP SetUserArray(ICredentialProviderUserArray* users) override;

private:
    ~PhoneUnlockProvider();
    void ClearCredentials();
    HRESULT RebuildCredentials();
    void StartProximityWatcher();
    void StopProximityWatcher();
    void NotifyProximityUnlock();

    volatile long referenceCount_ = 1;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO usageScenario_ = CPUS_INVALID;
    ICredentialProviderEvents* events_ = nullptr;
    UINT_PTR adviseContext_ = 0;
    ICredentialProviderUserArray* users_ = nullptr;
    std::vector<PhoneUnlockCredential*> credentials_;
    std::atomic<bool> proximityWatcherStopping_ = false;
    std::shared_ptr<std::atomic<bool>> proximityUnlockPending_ =
        std::make_shared<std::atomic<bool>>(false);
    std::thread proximityWatcher_;
    std::mutex eventsMutex_;
};
