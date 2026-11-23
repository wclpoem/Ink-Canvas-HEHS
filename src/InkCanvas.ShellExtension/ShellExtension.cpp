#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <thumbcache.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <algorithm>
#include <cstdint>
#include <new>
#include <vector>

using Microsoft::WRL::ComPtr;

// Both handlers use the Windows isolation hosts. This DLL has no managed or WinUI dependency.
static const CLSID ThumbnailId = {0x83ad3cdd,0x699a,0x4c40,{0xbf,0x31,0x0b,0x6f,0x56,0x4b,0x8a,0x91}};
static const CLSID PreviewId = {0x179cddb8,0x995a,0x40b5,{0x81,0xf9,0x1e,0xde,0xcf,0x07,0x24,0x5c}};
static HINSTANCE Module;
static long Objects = 0;
static const wchar_t* WindowClass = L"InkCanvas.IsolatedPreview.1";
static constexpr uint32_t MaximumCover = 2 * 1024 * 1024;

static uint16_t U16(const BYTE* p) { return p[0] | (p[1] << 8); }
static uint32_t U32(const BYTE* p) { return U16(p) | (uint32_t(U16(p + 2)) << 16); }
static uint32_t Big32(const BYTE* p) { return (uint32_t(p[0]) << 24) | (uint32_t(p[1]) << 16) | (uint32_t(p[2]) << 8) | p[3]; }

static HRESULT ReadAt(IStream* stream, uint64_t offset, BYTE* data, ULONG count)
{
    LARGE_INTEGER position; position.QuadPart = offset;
    HRESULT result = stream->Seek(position, STREAM_SEEK_SET, nullptr);
    if (FAILED(result)) return result;
    ULONG read = 0;
    result = stream->Read(data, count, &read);
    return SUCCEEDED(result) && read == count ? S_OK : STG_E_READFAULT;
}

// Seek directly through the ZIP directory. Never read board.json, image assets or video bytes.
static HRESULT ReadCover(IStream* stream, std::vector<BYTE>& png)
{
    STATSTG stat{};
    HRESULT result = stream->Stat(&stat, STATFLAG_NONAME);
    if (FAILED(result)) return result;
    const uint64_t length = stat.cbSize.QuadPart;
    if (length < 22) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    ULONG tailSize = static_cast<ULONG>(std::min<uint64_t>(length, 65557));
    std::vector<BYTE> tail(tailSize);
    result = ReadAt(stream, length - tailSize, tail.data(), tailSize);
    if (FAILED(result)) return result;
    const BYTE* end = nullptr;
    for (int pos = static_cast<int>(tailSize) - 22; pos >= 0; --pos)
    {
        if (U32(tail.data() + pos) == 0x06054b50 && static_cast<ULONG>(pos + 22 + U16(tail.data() + pos + 20)) == tailSize)
        { end = tail.data() + pos; break; }
    }
    if (!end || U16(end + 4) || U16(end + 6) || U16(end + 8) != U16(end + 10)) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
    uint64_t directory = U32(end + 16), directorySize = U32(end + 12);
    uint16_t count = U16(end + 10);
    // ZIP64 containers safely fall back to the document icon.
    if (count == 65535 || directorySize > 16 * 1024 * 1024 || directory + directorySize > length) return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
    uint64_t cursor = directory;
    bool found = false;
    for (uint32_t i = 0; i < count; ++i)
    {
        BYTE header[46];
        if (cursor + sizeof(header) > directory + directorySize) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        result = ReadAt(stream, cursor, header, sizeof(header));
        if (FAILED(result) || U32(header) != 0x02014b50) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        uint16_t nameSize = U16(header + 28);
        uint64_t next = cursor + 46 + nameSize + U16(header + 30) + U16(header + 32);
        if (next > directory + directorySize) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        constexpr char name[] = "preview/cover.png";
        if (nameSize == sizeof(name) - 1)
        {
            BYTE entryName[sizeof(name) - 1];
            result = ReadAt(stream, cursor + 46, entryName, sizeof(entryName));
            if (FAILED(result)) return result;
            if (!memcmp(entryName, name, sizeof(entryName)))
            {
                if (found || U16(header + 10) != 0 || (U16(header + 8) & 1)) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
                uint32_t size = U32(header + 24);
                if (size < 33 || size > MaximumCover || size != U32(header + 20)) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
                BYTE local[30];
                uint64_t offset = U32(header + 42);
                if (offset + sizeof(local) > directory) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
                result = ReadAt(stream, offset, local, sizeof(local));
                if (FAILED(result) || U32(local) != 0x04034b50 || U16(local + 8) != 0 || U16(local + 26) != nameSize) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
                offset += 30 + U16(local + 26) + U16(local + 28);
                if (offset + size > directory) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
                png.resize(size);
                result = ReadAt(stream, offset, png.data(), size);
                if (FAILED(result)) return result;
                const BYTE signature[] = {137,80,78,71,13,10,26,10};
                if (memcmp(png.data(), signature, 8) || memcmp(png.data() + 12, "IHDR", 4)) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
                uint64_t width = Big32(png.data() + 16), height = Big32(png.data() + 20);
                if (!width || !height || width > 4096 || height > 4096 || width * height > 8 * 1024 * 1024) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
                found = true;
            }
        }
        cursor = next;
    }
    return found ? S_OK : HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
}

static HRESULT DecodeCover(IStream* input, UINT edge, HBITMAP* output)
{
    *output = nullptr;
    std::vector<BYTE> png;
    HRESULT result = ReadCover(input, png);
    if (FAILED(result)) return result;
    ComPtr<IWICImagingFactory> factory;
    result = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
    if (FAILED(result)) return result;
    ComPtr<IStream> stream; stream.Attach(SHCreateMemStream(png.data(), static_cast<UINT>(png.size())));
    if (!stream) return E_OUTOFMEMORY;
    ComPtr<IWICBitmapDecoder> decoder;
    result = factory->CreateDecoderFromStream(stream.Get(), nullptr, WICDecodeMetadataCacheOnDemand, &decoder);
    if (FAILED(result)) return result;
    ComPtr<IWICBitmapFrameDecode> frame;
    result = decoder->GetFrame(0, &frame);
    if (FAILED(result)) return result;
    UINT sourceWidth, sourceHeight;
    result = frame->GetSize(&sourceWidth, &sourceHeight);
    if (FAILED(result) || !sourceWidth || !sourceHeight) return E_FAIL;
    edge = std::clamp(edge, 1u, 1024u);
    double scale = std::min(1.0, double(edge) / std::max(sourceWidth, sourceHeight));
    UINT width = std::max(1u, UINT(sourceWidth * scale)), height = std::max(1u, UINT(sourceHeight * scale));
    ComPtr<IWICBitmapScaler> scaler;
    result = factory->CreateBitmapScaler(&scaler);
    if (FAILED(result)) return result;
    result = scaler->Initialize(frame.Get(), width, height, WICBitmapInterpolationModeFant);
    if (FAILED(result)) return result;
    ComPtr<IWICFormatConverter> converter;
    result = factory->CreateFormatConverter(&converter);
    if (FAILED(result)) return result;
    result = converter->Initialize(scaler.Get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom);
    if (FAILED(result)) return result;
    BITMAPINFO info{};
    info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER); info.bmiHeader.biWidth = width; info.bmiHeader.biHeight = -LONG(height);
    info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32; info.bmiHeader.biCompression = BI_RGB;
    void* pixels = nullptr;
    HBITMAP bitmap = CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
    if (!bitmap) return E_OUTOFMEMORY;
    result = converter->CopyPixels(nullptr, width * 4, width * height * 4, static_cast<BYTE*>(pixels));
    if (FAILED(result)) { DeleteObject(bitmap); return result; }
    *output = bitmap; return S_OK;
}

class Handler final : public IInitializeWithStream, public IThumbnailProvider, public IPreviewHandler,
    public IObjectWithSite, public IOleWindow, public IPreviewHandlerVisuals
{
    long references = 1;
    ComPtr<IStream> stream;
    ComPtr<IUnknown> site;
    HWND parent = nullptr, window = nullptr;
    RECT rectangle{};
    HBITMAP bitmap = nullptr;
    HFONT font = nullptr;
    COLORREF background = GetSysColor(COLOR_WINDOW), text = GetSysColor(COLOR_WINDOWTEXT);
    bool missing = false;
    static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam)
    {
        if (message == WM_NCCREATE)
            SetWindowLongPtr(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(reinterpret_cast<CREATESTRUCT*>(lparam)->lpCreateParams));
        auto self = reinterpret_cast<Handler*>(GetWindowLongPtr(hwnd, GWLP_USERDATA));
        if (!self) return DefWindowProc(hwnd, message, wparam, lparam);
        if (message == WM_ERASEBKGND) return 1;
        if (message == WM_PAINT)
        {
            PAINTSTRUCT paint; HDC dc = BeginPaint(hwnd, &paint);
            RECT client; GetClientRect(hwnd, &client);
            HBRUSH brush = CreateSolidBrush(self->background); FillRect(dc, &client, brush); DeleteObject(brush);
            if (self->bitmap)
            {
                BITMAP shape; GetObject(self->bitmap, sizeof(shape), &shape);
                int availableWidth = std::max(1L, client.right - 32), availableHeight = std::max(1L, client.bottom - 56);
                double scale = std::min(double(availableWidth) / shape.bmWidth, double(availableHeight) / shape.bmHeight);
                int width = int(shape.bmWidth * scale), height = int(shape.bmHeight * scale);
                HDC memory = CreateCompatibleDC(dc); HGDIOBJ previous = SelectObject(memory, self->bitmap);
                SetStretchBltMode(dc, HALFTONE); SetBrushOrgEx(dc, 0, 0, nullptr);
                StretchBlt(dc, (client.right - width) / 2, (client.bottom - height - 28) / 2, width, height, memory, 0, 0, shape.bmWidth, shape.bmHeight, SRCCOPY);
                SelectObject(memory, previous); DeleteDC(memory);
            }
            auto previousFont = self->font ? SelectObject(dc, self->font) : nullptr;
            SetBkMode(dc, TRANSPARENT); ::SetTextColor(dc, self->text);
            RECT label = client; label.left += 16; label.right -= 16;
            if (self->bitmap) label.top = std::max(0L, client.bottom - 32);
            const wchar_t* caption = self->missing ? L"此文稿尚无封面预览\n请使用 Ink Canvas 重新保存文稿" : L"Ink Canvas · 首页预览";
            DrawText(dc, caption, -1, &label, DT_CENTER | DT_WORDBREAK | DT_NOPREFIX);
            if (previousFont) SelectObject(dc, previousFont);
            EndPaint(hwnd, &paint); return 0;
        }
        if (message == WM_SIZE) { InvalidateRect(hwnd, nullptr, FALSE); return 0; }
        return DefWindowProc(hwnd, message, wparam, lparam);
    }
public:
    Handler() { InterlockedIncrement(&Objects); }
    ~Handler() { Unload(); InterlockedDecrement(&Objects); }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** output) override
    {
        if (!output) return E_POINTER;
        *output = nullptr;
        if (iid == IID_IUnknown || iid == __uuidof(IInitializeWithStream)) *output = static_cast<IInitializeWithStream*>(this);
        else if (iid == __uuidof(IThumbnailProvider)) *output = static_cast<IThumbnailProvider*>(this);
        else if (iid == __uuidof(IPreviewHandler)) *output = static_cast<IPreviewHandler*>(this);
        else if (iid == __uuidof(IObjectWithSite)) *output = static_cast<IObjectWithSite*>(this);
        else if (iid == __uuidof(IOleWindow)) *output = static_cast<IOleWindow*>(this);
        else if (iid == __uuidof(IPreviewHandlerVisuals)) *output = static_cast<IPreviewHandlerVisuals*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&references); }
    IFACEMETHODIMP_(ULONG) Release() override { ULONG count = InterlockedDecrement(&references); if (!count) delete this; return count; }
    IFACEMETHODIMP Initialize(IStream* input, DWORD mode) override
    {
        if (!input) return E_POINTER;
        if (stream) return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
        if ((mode & 3) != STGM_READ) return STG_E_ACCESSDENIED;
        stream = input; return S_OK;
    }
    IFACEMETHODIMP GetThumbnail(UINT size, HBITMAP* output, WTS_ALPHATYPE* alpha) override
    {
        if (!output || !alpha) return E_POINTER;
        *output = nullptr; *alpha = WTSAT_UNKNOWN;
        if (!stream) return E_UNEXPECTED;
        try { HRESULT result = DecodeCover(stream.Get(), size, output); if (SUCCEEDED(result)) *alpha = WTSAT_ARGB; return result; }
        catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
        catch (...) { return E_FAIL; }
    }
    IFACEMETHODIMP SetWindow(HWND hwnd, const RECT* area) override
    {
        if (!area) return E_POINTER;
        parent = hwnd; rectangle = *area;
        if (window) SetParent(window, hwnd);
        return SetRect(area);
    }
    IFACEMETHODIMP SetRect(const RECT* area) override
    {
        if (!area) return E_POINTER;
        rectangle = *area;
        if (window) SetWindowPos(window, nullptr, area->left, area->top, std::max(0L, area->right - area->left), std::max(0L, area->bottom - area->top), SWP_NOZORDER | SWP_NOACTIVATE);
        return S_OK;
    }
    IFACEMETHODIMP DoPreview() override
    {
        if (!stream || !parent) return E_UNEXPECTED;
        if (window) return S_OK;
        try
        {
            missing = FAILED(DecodeCover(stream.Get(), 1024, &bitmap));
            WNDCLASSEX info{sizeof(info)}; info.lpfnWndProc = WindowProc; info.hInstance = Module;
            info.hCursor = LoadCursor(nullptr, IDC_ARROW); info.lpszClassName = WindowClass;
            if (!RegisterClassEx(&info) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return HRESULT_FROM_WIN32(GetLastError());
            window = CreateWindowEx(0, WindowClass, L"Ink Canvas preview", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
                rectangle.left, rectangle.top, std::max(0L, rectangle.right - rectangle.left), std::max(0L, rectangle.bottom - rectangle.top), parent, nullptr, Module, this);
            return window ? S_OK : HRESULT_FROM_WIN32(GetLastError());
        }
        catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
        catch (...) { return E_FAIL; }
    }
    IFACEMETHODIMP Unload() override
    {
        if (window) { DestroyWindow(window); window = nullptr; UnregisterClass(WindowClass, Module); }
        if (bitmap) { DeleteObject(bitmap); bitmap = nullptr; }
        if (font) { DeleteObject(font); font = nullptr; }
        stream.Reset(); site.Reset(); parent = nullptr; return S_OK;
    }
    IFACEMETHODIMP SetFocus() override { if (window) ::SetFocus(window); return S_OK; }
    IFACEMETHODIMP QueryFocus(HWND* hwnd) override { if (!hwnd) return E_POINTER; *hwnd = GetFocus(); return S_OK; }
    IFACEMETHODIMP TranslateAccelerator(MSG* message) override
    {
        ComPtr<IPreviewHandlerFrame> frame;
        return site && SUCCEEDED(site.As(&frame)) ? frame->TranslateAccelerator(message) : S_FALSE;
    }
    IFACEMETHODIMP SetSite(IUnknown* value) override { site = value; return S_OK; }
    IFACEMETHODIMP GetSite(REFIID iid, void** output) override { if (!output) return E_POINTER; *output = nullptr; return site ? site->QueryInterface(iid, output) : E_FAIL; }
    IFACEMETHODIMP GetWindow(HWND* hwnd) override { if (!hwnd) return E_POINTER; *hwnd = window; return S_OK; }
    IFACEMETHODIMP ContextSensitiveHelp(BOOL) override { return E_NOTIMPL; }
    IFACEMETHODIMP SetBackgroundColor(COLORREF value) override { background = value; if (window) InvalidateRect(window, nullptr, FALSE); return S_OK; }
    IFACEMETHODIMP SetFont(const LOGFONTW* value) override
    {
        if (!value) return E_POINTER;
        auto replacement = CreateFontIndirectW(value); if (!replacement) return E_OUTOFMEMORY;
        if (font) DeleteObject(font);
        font = replacement; if (window) InvalidateRect(window, nullptr, FALSE); return S_OK;
    }
    IFACEMETHODIMP SetTextColor(COLORREF value) override { text = value; if (window) InvalidateRect(window, nullptr, FALSE); return S_OK; }
};

class Factory final : public IClassFactory
{
    long references = 1;
public:
    Factory() { InterlockedIncrement(&Objects); }
    ~Factory() { InterlockedDecrement(&Objects); }
    IFACEMETHODIMP QueryInterface(REFIID iid, void** output) override
    {
        if (!output) return E_POINTER; *output = nullptr;
        if (iid != IID_IUnknown && iid != IID_IClassFactory) return E_NOINTERFACE;
        *output = static_cast<IClassFactory*>(this); AddRef(); return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&references); }
    IFACEMETHODIMP_(ULONG) Release() override { ULONG count = InterlockedDecrement(&references); if (!count) delete this; return count; }
    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** output) override
    {
        if (!output) return E_POINTER; *output = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto handler = new(std::nothrow) Handler(); if (!handler) return E_OUTOFMEMORY;
        HRESULT result = handler->QueryInterface(iid, output); handler->Release(); return result;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override { if (lock) InterlockedIncrement(&Objects); else InterlockedDecrement(&Objects); return S_OK; }
};

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** output)
{
    if (!output) return E_POINTER; *output = nullptr;
    if (clsid != ThumbnailId && clsid != PreviewId) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new(std::nothrow) Factory(); if (!factory) return E_OUTOFMEMORY;
    HRESULT result = factory->QueryInterface(iid, output); factory->Release(); return result;
}
extern "C" HRESULT __stdcall DllCanUnloadNow() { return Objects == 0 ? S_OK : S_FALSE; }
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH) { Module = module; DisableThreadLibraryCalls(module); }
    return TRUE;
}
