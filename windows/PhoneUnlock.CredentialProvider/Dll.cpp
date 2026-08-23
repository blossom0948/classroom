#include <windows.h>
#include <unknwn.h>

#include "Guids.h"
#include "Provider.h"
#include <new>

namespace
{
volatile long moduleReferences = 0;

class PhoneUnlockClassFactory final : public IClassFactory
{
public:
    PhoneUnlockClassFactory() { InterlockedIncrement(&moduleReferences); }

    IFACEMETHODIMP QueryInterface(REFIID interfaceId, void** object) override
    {
        if (object == nullptr) return E_INVALIDARG;
        *object = nullptr;
        if (interfaceId != IID_IUnknown && interfaceId != IID_IClassFactory) return E_NOINTERFACE;
        *object = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }

    IFACEMETHODIMP_(ULONG) AddRef() override
    {
        return static_cast<ULONG>(InterlockedIncrement(&referenceCount_));
    }

    IFACEMETHODIMP_(ULONG) Release() override
    {
        const long count = InterlockedDecrement(&referenceCount_);
        if (count == 0) delete this;
        return static_cast<ULONG>(count);
    }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID interfaceId, void** object) override
    {
        if (outer != nullptr) return CLASS_E_NOAGGREGATION;
        auto provider = new (std::nothrow) PhoneUnlockProvider();
        if (provider == nullptr) return E_OUTOFMEMORY;
        const HRESULT result = provider->QueryInterface(interfaceId, object);
        provider->Release();
        return result;
    }

    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&moduleReferences);
        else InterlockedDecrement(&moduleReferences);
        return S_OK;
    }

private:
    ~PhoneUnlockClassFactory() { InterlockedDecrement(&moduleReferences); }
    volatile long referenceCount_ = 1;
};
}

void DllAddRef() { InterlockedIncrement(&moduleReferences); }
void DllRelease() { InterlockedDecrement(&moduleReferences); }

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    return moduleReferences == 0 ? S_OK : S_FALSE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID classId, REFIID interfaceId, void** object)
{
    if (classId != CLSID_PhoneUnlockProvider) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new (std::nothrow) PhoneUnlockClassFactory();
    if (factory == nullptr) return E_OUTOFMEMORY;
    const HRESULT result = factory->QueryInterface(interfaceId, object);
    factory->Release();
    return result;
}
