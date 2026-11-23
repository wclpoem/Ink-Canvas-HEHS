#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <thumbcache.h>
#include <wrl/client.h>
#include <cstdio>
#include <initializer_list>
using Microsoft::WRL::ComPtr;
static const CLSID ThumbnailId = {0x83ad3cdd,0x699a,0x4c40,{0xbf,0x31,0x0b,0x6f,0x56,0x4b,0x8a,0x91}};
static const CLSID PreviewId = {0x179cddb8,0x995a,0x40b5,{0x81,0xf9,0x1e,0xde,0xcf,0x07,0x24,0x5c}};

class CountingStream final : public IStream
{
    long references = 1;
    ComPtr<IStream> inner;
public:
    unsigned long long BytesRead = 0;
    CountingStream(IStream* source) : inner(source) { }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** output) override
    { *output = nullptr; if (id != IID_IUnknown && id != IID_IStream && id != IID_ISequentialStream) return E_NOINTERFACE; *output = this; AddRef(); return S_OK; }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
    ULONG STDMETHODCALLTYPE Release() override { auto value = InterlockedDecrement(&references); if (!value) delete this; return value; }
    HRESULT STDMETHODCALLTYPE Read(void* data, ULONG size, ULONG* read) override
    { ULONG count = 0; auto result = inner->Read(data, size, &count); BytesRead += count; if (read) *read = count; return result; }
    HRESULT STDMETHODCALLTYPE Write(const void*, ULONG, ULONG*) override { return STG_E_ACCESSDENIED; }
    HRESULT STDMETHODCALLTYPE Seek(LARGE_INTEGER offset, DWORD origin, ULARGE_INTEGER* position) override { return inner->Seek(offset, origin, position); }
    HRESULT STDMETHODCALLTYPE SetSize(ULARGE_INTEGER) override { return STG_E_ACCESSDENIED; }
    HRESULT STDMETHODCALLTYPE CopyTo(IStream*, ULARGE_INTEGER, ULARGE_INTEGER*, ULARGE_INTEGER*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Commit(DWORD) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Revert() override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE LockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE UnlockRegion(ULARGE_INTEGER, ULARGE_INTEGER, DWORD) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Stat(STATSTG* info, DWORD flags) override { return inner->Stat(info, flags); }
    HRESULT STDMETHODCALLTYPE Clone(IStream**) override { return E_NOTIMPL; }
};

int wmain(int argc, wchar_t** argv)
{
    if (argc != 3 && argc != 4) { wprintf(L"ShellExtensionCheck <dll> <fixture.icink> [--registered]\n"); return 2; }
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    auto module = LoadLibraryW(argv[1]); if (!module) return 3;
    using ClassObject = HRESULT(WINAPI*)(REFCLSID, REFIID, void**);
    auto factoryFunction = reinterpret_cast<ClassObject>(GetProcAddress(module, "DllGetClassObject"));
    auto unload = reinterpret_cast<HRESULT(WINAPI*)()>(GetProcAddress(module, "DllCanUnloadNow"));
    int failures = 0;
    auto check = [&](const char* label, bool pass) { printf("%s %s\n", pass ? "PASS" : "FAIL", label); if (!pass) failures++; };
    check("empty module can unload", unload() == S_OK);
    {
        ComPtr<IStream> file; SHCreateStreamOnFileEx(argv[2], STGM_READ | STGM_SHARE_DENY_NONE, 0, FALSE, nullptr, &file);
        if (!file) return 4;
        ComPtr<CountingStream> stream; stream.Attach(new CountingStream(file.Get()));
        ComPtr<IClassFactory> factory;
        check("thumbnail factory", SUCCEEDED(factoryFunction(ThumbnailId, IID_PPV_ARGS(&factory))));
        if (!factory) return 5;
        ComPtr<IInitializeWithStream> init;
        check("create stream initializer", SUCCEEDED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&init))));
        check("read-only stream init", SUCCEEDED(init->Initialize(stream.Get(), STGM_READ)));
        check("reject duplicate init", FAILED(init->Initialize(stream.Get(), STGM_READ)));
        ComPtr<IThumbnailProvider> thumbnail; init.As(&thumbnail);
        for (UINT size : {32u, 256u, 8192u})
        {
            HBITMAP bitmap = nullptr; WTS_ALPHATYPE alpha{};
            auto result = thumbnail->GetThumbnail(size, &bitmap, &alpha);
            BITMAP shape{}; if (bitmap) GetObject(bitmap, sizeof(shape), &shape);
            check("thumbnail size and alpha", SUCCEEDED(result) && bitmap && shape.bmWidth <= 1024 && shape.bmHeight <= 1024 && alpha == WTSAT_ARGB);
            if (bitmap) DeleteObject(bitmap);
        }
        check("three thumbnails skip large embedded video", stream->BytesRead < 3 * (2 * 1024 * 1024 + 100000));
        printf("thumbnail bytes read %llu\n", stream->BytesRead);
        check("factory holds unload gate", unload() == S_FALSE);
    }
    check("thumbnail resources released", unload() == S_OK);
    {
        ComPtr<IClassFactory> factory; factoryFunction(PreviewId, IID_PPV_ARGS(&factory));
        ComPtr<IInitializeWithStream> init; factory->CreateInstance(nullptr, IID_PPV_ARGS(&init));
        ComPtr<IStream> file; SHCreateStreamOnFileEx(argv[2], STGM_READ | STGM_SHARE_DENY_NONE, 0, FALSE, nullptr, &file);
        init->Initialize(file.Get(), STGM_READ);
        ComPtr<IPreviewHandler> preview; init.As(&preview);
        HWND host = CreateWindowEx(0, L"STATIC", L"Preview test host", WS_OVERLAPPED, 0, 0, 600, 400, nullptr, nullptr, GetModuleHandle(nullptr), nullptr);
        RECT area{0,0,600,400}; preview->SetWindow(host, &area);
        check("native preview window", SUCCEEDED(preview->DoPreview()));
        ComPtr<IOleWindow> window; init.As(&window); HWND child = nullptr; window->GetWindow(&child);
        check("preview is parented to shell host", child && GetParent(child) == host);
        area = {0,0,320,240}; check("preview resizes", SUCCEEDED(preview->SetRect(&area)));
        RECT actual{}; GetClientRect(child, &actual); check("precise resized bounds", actual.right == 320 && actual.bottom == 240);
        check("preview unload", SUCCEEDED(preview->Unload()));
        check("preview destroys child", !IsWindow(child)); DestroyWindow(host);
    }
    check("preview resources released", unload() == S_OK);
    {
        const BYTE invalid[] = {0x50, 0x4b, 0, 0, 0};
        ComPtr<IStream> stream; stream.Attach(SHCreateMemStream(invalid, sizeof(invalid)));
        ComPtr<IClassFactory> factory; factoryFunction(ThumbnailId, IID_PPV_ARGS(&factory));
        ComPtr<IInitializeWithStream> init; factory->CreateInstance(nullptr, IID_PPV_ARGS(&init)); init->Initialize(stream.Get(), STGM_READ);
        ComPtr<IThumbnailProvider> provider; init.As(&provider); HBITMAP bitmap = nullptr; WTS_ALPHATYPE alpha{};
        check("truncated package fails cleanly", FAILED(provider->GetThumbnail(256, &bitmap, &alpha)) && !bitmap);
    }
    check("all COM references released", unload() == S_OK);
    if (argc == 4 && !wcscmp(argv[3], L"--registered"))
    {
        ComPtr<IShellItem> item;
        auto result = SHCreateItemFromParsingName(argv[2], nullptr, IID_PPV_ARGS(&item));
        ComPtr<IThumbnailCache> cache;
        if (SUCCEEDED(result)) result = CoCreateInstance(CLSID_LocalThumbnailCache, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&cache));
        ComPtr<ISharedBitmap> shared; WTS_CACHEFLAGS flags{}; WTS_THUMBNAILID id{};
        if (SUCCEEDED(result)) result = cache->GetThumbnail(item.Get(), 256, WTS_FORCEEXTRACTION, &shared, &flags, &id);
        check("registered Shell thumbnail extraction through Windows cache", SUCCEEDED(result) && shared);
        printf("Windows cache extraction HRESULT 0x%08lx\n", result);
        ComPtr<IPreviewHandler> isolated;
        result = CoCreateInstance(PreviewId, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&isolated));
        check("registered preview activates in Windows surrogate", SUCCEEDED(result));
        printf("Windows preview activation HRESULT 0x%08lx\n", result);
        if (isolated)
        {
            ComPtr<IInitializeWithStream> init; isolated.As(&init);
            ComPtr<IStream> source; SHCreateStreamOnFileEx(argv[2], STGM_READ | STGM_SHARE_DENY_NONE, 0, FALSE, nullptr, &source);
            result = init->Initialize(source.Get(), STGM_READ);
            HWND host = CreateWindowEx(0, L"STATIC", L"Isolated preview test host", WS_OVERLAPPED, 0, 0, 600, 400, nullptr, nullptr, GetModuleHandle(nullptr), nullptr);
            RECT area{0,0,600,400};
            if (SUCCEEDED(result)) result = isolated->SetWindow(host, &area);
            if (SUCCEEDED(result)) result = isolated->DoPreview();
            check("isolated preview accepts stream and host window", SUCCEEDED(result));
            printf("Windows isolated preview HRESULT 0x%08lx\n", result);
            isolated->Unload(); DestroyWindow(host);
        }
    }
    FreeLibrary(module); CoUninitialize(); return failures ? 1 : 0;
}
