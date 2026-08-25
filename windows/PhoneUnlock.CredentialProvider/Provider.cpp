#include "Provider.h"

#include "Credential.h"
#include "Helpers.h"
#include <new>
#include <chrono>

namespace
{
constexpr wchar_t ProximityUnlockEventName[] = L"Global\\PhoneUnlock.ProximityUnlock";
}

void DllAddRef();
void DllRelease();

PhoneUnlockProvider::PhoneUnlockProvider()
{
    DllAddRef();
}

PhoneUnlockProvider::~PhoneUnlockProvider()
{
    UnAdvise();
    ClearCredentials();
    if (users_ != nullptr) users_->Release();
    DllRelease();
}

HRESULT PhoneUnlockProvider::QueryInterface(REFIID interfaceId, void** object)
{
    if (object == nullptr) return E_INVALIDARG;
    *object = nullptr;
    if (interfaceId == IID_IUnknown || interfaceId == IID_ICredentialProvider)
    {
        *object = static_cast<ICredentialProvider*>(this);
    }
    else if (interfaceId == IID_ICredentialProviderSetUserArray)
    {
        *object = static_cast<ICredentialProviderSetUserArray*>(this);
    }
    else
    {
        return E_NOINTERFACE;
    }
    AddRef();
    return S_OK;
}

ULONG PhoneUnlockProvider::AddRef() { return static_cast<ULONG>(InterlockedIncrement(&referenceCount_)); }
ULONG PhoneUnlockProvider::Release()
{
    const long count = InterlockedDecrement(&referenceCount_);
    if (count == 0) delete this;
    return static_cast<ULONG>(count);
}

HRESULT PhoneUnlockProvider::SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO usageScenario, DWORD)
{
    if (usageScenario != CPUS_LOGON && usageScenario != CPUS_UNLOCK_WORKSTATION)
    {
        return E_NOTIMPL;
    }
    usageScenario_ = usageScenario;
    return users_ == nullptr ? S_OK : RebuildCredentials();
}

HRESULT PhoneUnlockProvider::SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*)
{
    return E_NOTIMPL;
}

HRESULT PhoneUnlockProvider::Advise(ICredentialProviderEvents* events, UINT_PTR adviseContext)
{
    {
        std::lock_guard lock(eventsMutex_);
        if (events_ != nullptr) events_->Release();
        events_ = events;
        adviseContext_ = adviseContext;
        if (events_ != nullptr) events_->AddRef();
    }
    StartProximityWatcher();
    return S_OK;
}

HRESULT PhoneUnlockProvider::UnAdvise()
{
    {
        std::lock_guard lock(eventsMutex_);
        if (events_ != nullptr)
        {
            events_->Release();
            events_ = nullptr;
        }
        adviseContext_ = 0;
    }
    StopProximityWatcher();
    return S_OK;
}

HRESULT PhoneUnlockProvider::GetFieldDescriptorCount(DWORD* count)
{
    if (count == nullptr) return E_INVALIDARG;
    *count = FieldCount;
    return S_OK;
}

HRESULT PhoneUnlockProvider::GetFieldDescriptorAt(
    DWORD index,
    CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** descriptor)
{
    if (index >= FieldCount) return E_INVALIDARG;
    return CopyFieldDescriptor(PhoneUnlockFieldDescriptors[index], descriptor);
}

HRESULT PhoneUnlockProvider::GetCredentialCount(
    DWORD* count,
    DWORD* defaultCredential,
    BOOL* autoLogonWithDefault)
{
    if (count == nullptr || defaultCredential == nullptr || autoLogonWithDefault == nullptr) return E_INVALIDARG;
    *count = static_cast<DWORD>(credentials_.size());
    const bool proximityUnlock = proximityUnlockPending_.exchange(false);
    *defaultCredential = credentials_.empty() ? CREDENTIAL_PROVIDER_NO_DEFAULT : 0;
    *autoLogonWithDefault = proximityUnlock ? TRUE : FALSE;
    return S_OK;
}

HRESULT PhoneUnlockProvider::GetCredentialAt(DWORD index, ICredentialProviderCredential** credential)
{
    if (credential == nullptr || index >= credentials_.size()) return E_INVALIDARG;
    *credential = static_cast<ICredentialProviderCredential2*>(credentials_[index]);
    (*credential)->AddRef();
    return S_OK;
}

HRESULT PhoneUnlockProvider::SetUserArray(ICredentialProviderUserArray* users)
{
    if (users == nullptr) return E_INVALIDARG;
    if (users_ != nullptr) users_->Release();
    users_ = users;
    users_->AddRef();
    return usageScenario_ == CPUS_INVALID ? S_OK : RebuildCredentials();
}

void PhoneUnlockProvider::ClearCredentials()
{
    for (PhoneUnlockCredential* credential : credentials_)
    {
        credential->Release();
    }
    credentials_.clear();
}

HRESULT PhoneUnlockProvider::RebuildCredentials()
{
    ClearCredentials();
    if (users_ == nullptr) return S_OK;

    DWORD count = 0;
    HRESULT result = users_->GetCount(&count);
    if (FAILED(result)) return result;

    try
    {
        credentials_.reserve(count);
    }
    catch (const std::bad_alloc&)
    {
        return E_OUTOFMEMORY;
    }

    for (DWORD index = 0; index < count; ++index)
    {
        ICredentialProviderUser* user = nullptr;
        result = users_->GetAt(index, &user);
        if (FAILED(result))
        {
            ClearCredentials();
            return result;
        }
        PWSTR sid = nullptr;
        result = user->GetSid(&sid);
        if (SUCCEEDED(result))
        {
            auto credential = new (std::nothrow) PhoneUnlockCredential();
            if (credential == nullptr)
            {
                result = E_OUTOFMEMORY;
            }
            else
            {
                result = credential->Initialize(usageScenario_, sid);
                if (SUCCEEDED(result))
                {
                    try
                    {
                        credentials_.push_back(credential);
                    }
                    catch (const std::bad_alloc&)
                    {
                        result = E_OUTOFMEMORY;
                        credential->Release();
                    }
                }
                else
                {
                    credential->Release();
                }
            }
        }
        CoTaskMemFree(sid);
        user->Release();
        if (FAILED(result))
        {
            ClearCredentials();
            return result;
        }
    }
    return S_OK;
}

void PhoneUnlockProvider::StartProximityWatcher()
{
    StopProximityWatcher();
    proximityWatcherStopping_ = false;
    proximityWatcher_ = std::thread([this]()
    {
        while (!proximityWatcherStopping_)
        {
            HANDLE event = OpenEventW(SYNCHRONIZE, FALSE, ProximityUnlockEventName);
            if (event == nullptr)
            {
                std::this_thread::sleep_for(std::chrono::milliseconds(1000));
                continue;
            }

            const DWORD result = WaitForSingleObject(event, 1000);
            CloseHandle(event);
            if (result == WAIT_OBJECT_0 && !proximityWatcherStopping_)
            {
                NotifyProximityUnlock();
            }
        }
    });
}

void PhoneUnlockProvider::StopProximityWatcher()
{
    proximityWatcherStopping_ = true;
    if (proximityWatcher_.joinable())
    {
        proximityWatcher_.join();
    }
}

void PhoneUnlockProvider::NotifyProximityUnlock()
{
    proximityUnlockPending_ = true;

    ICredentialProviderEvents* events = nullptr;
    UINT_PTR adviseContext = 0;
    {
        std::lock_guard lock(eventsMutex_);
        if (events_ != nullptr)
        {
            events = events_;
            events->AddRef();
            adviseContext = adviseContext_;
        }
    }

    if (events != nullptr)
    {
        events->CredentialsChanged(adviseContext);
        events->Release();
    }
}
