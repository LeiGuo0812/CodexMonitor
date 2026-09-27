#include "render.h"
#include "platform.h"
#include <d2d1helper.h>
namespace cqm
{
static void check(HRESULT h)
{
    if (FAILED(h))
        throw std::runtime_error("DRAWING_INITIALIZATION_FAILED");
}
Color color(const std::wstring &hex, float a)
{
    auto v = validColor(hex) ? wcstoul(hex.c_str() + 1, nullptr, 16) : 0;
    return D2D1::ColorF((UINT32)v, a);
}
Color alpha(Color c, float a)
{
    c.a = a;
    return c;
}
Color blend(Color a, Color b, float t)
{
    return {a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t};
}
Palette palette(const Settings &s)
{
    bool dark = s.mode == 2 || (s.mode == 0 && systemDark(true));
    const wchar_t *light[4][5] = {{L"#E9F3FF", L"#12263F", L"#506887", L"#256FD4", L"#8854CF"},
                                  {L"#F5E2C9", L"#3B291D", L"#795B43", L"#A04F21", L"#197F93"},
                                  {L"#DCEAE4", L"#18352C", L"#506C61", L"#19785C", L"#986B23"},
                                  {L"#E9DEFC", L"#31234B", L"#716084", L"#7950BD", L"#157F84"}};
    const wchar_t *deep[4][5] = {{L"#172D44", L"#EAF5FF", L"#B0CBE1", L"#7EBEFF", L"#C8A1FF"},
                                 {L"#38251D", L"#FFF1DF", L"#D7B99A", L"#F2B16D", L"#6ED8E1"},
                                 {L"#202828", L"#EFF8F4", L"#AEC5BE", L"#63D7B1", L"#E5AF65"},
                                 {L"#191B39", L"#F1EDFF", L"#BCB3DE", L"#BA9FFF", L"#50D9D0"}};
    Palette p;
    p.dark = dark;
    p.contrast = highContrast();
    p.glass = transparencyEnabled() && !p.contrast;
    if (s.preset < 4)
    {
        auto &c = dark ? deep[s.preset] : light[s.preset];
        p.background = color(c[0]);
        p.primary = color(c[1]);
        p.secondary = color(c[2]);
        p.accent = color(c[3]);
        p.weekly = color(c[4]);
    }
    else
    {
        p.background = color(s.background);
        p.primary = color(s.primary);
        p.secondary = color(s.secondary);
        p.accent = color(s.accent);
        p.weekly = color(L"#8653D9");
        p.dark = p.background.r * .2126f + p.background.g * .7152f + p.background.b * .0722f < .5f;
    }
    if (p.contrast)
    {
        auto sys = [](int index)
        {
            COLORREF c = GetSysColor(index);
            return Color{GetRValue(c) / 255.f, GetGValue(c) / 255.f, GetBValue(c) / 255.f, 1};
        };
        p.background = sys(COLOR_WINDOW);
        p.primary = p.secondary = sys(COLOR_WINDOWTEXT);
        p.accent = p.weekly = sys(COLOR_HIGHLIGHT);
    }
    return p;
}
Drawing::Drawing()
{
    check(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, factory_.GetAddressOf()));
    check(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                              (IUnknown **)write_.GetAddressOf()));
}
IDWriteTextFormat *Drawing::format(float size, bool bold)
{
    auto key = std::make_pair((int)std::round(size * 10), bold);
    auto found = formats_.find(key);
    if (found != formats_.end())
        return found->second.Get();
    ComPtr<IDWriteTextFormat> f;
    check(write_->CreateTextFormat(L"Segoe UI Variable Text", nullptr,
                                   bold ? DWRITE_FONT_WEIGHT_SEMI_BOLD : DWRITE_FONT_WEIGHT_NORMAL,
                                   DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, size, L"zh-CN", &f));
    f->SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP);
    formats_[key] = f;
    return f.Get();
}
D2D1_SIZE_F Drawing::measure(const std::wstring &text, float size, bool bold, float width)
{
    ComPtr<IDWriteTextLayout> layout;
    check(write_->CreateTextLayout(text.c_str(), (UINT32)text.size(), format(size, bold), width, 10000,
                                   &layout));
    DWRITE_TEXT_METRICS m{};
    layout->GetMetrics(&m);
    return {m.widthIncludingTrailingWhitespace, m.height};
}
Canvas::~Canvas()
{
    reset();
}
void Canvas::reset()
{
    brush_.Reset();
    windowTarget_.Reset();
    dcTarget_.Reset();
    target_ = nullptr;
    if (dc_)
    {
        SelectObject(dc_, previous_);
        DeleteObject(bitmap_);
        DeleteDC(dc_);
    }
    dc_ = nullptr;
    bitmap_ = nullptr;
    previous_ = nullptr;
    width_ = height_ = 0;
}
bool Canvas::begin(HWND h, bool layered, float scale, Color clear)
{
    hwnd_ = h;
    layered_ = layered;
    RECT r{};
    GetClientRect(h, &r);
    int w = r.right, hgt = r.bottom;
    if (w <= 0 || hgt <= 0)
        return false;
    if (layered)
    {
        if (!dc_ || w != width_ || hgt != height_)
        {
            reset();
            dc_ = CreateCompatibleDC(nullptr);
            BITMAPINFO bi{};
            bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            bi.bmiHeader.biWidth = w;
            bi.bmiHeader.biHeight = -hgt;
            bi.bmiHeader.biPlanes = 1;
            bi.bmiHeader.biBitCount = 32;
            bi.bmiHeader.biCompression = BI_RGB;
            void *bits = nullptr;
            bitmap_ = CreateDIBSection(dc_, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
            if (!dc_ || !bitmap_)
            {
                reset();
                return false;
            }
            previous_ = SelectObject(dc_, bitmap_);
            width_ = w;
            height_ = hgt;
        }
        if (!dcTarget_)
        {
            auto props = D2D1::RenderTargetProperties(
                D2D1_RENDER_TARGET_TYPE_SOFTWARE,
                D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
            check(drawing_.factory()->CreateDCRenderTarget(&props, &dcTarget_));
        }
        check(dcTarget_->BindDC(dc_, &r));
        target_ = dcTarget_.Get();
    }
    else
    {
        // These compact, infrequently changing panels do not need a persistent D3D device.
        // DWM still supplies the system acrylic; Direct2D rasterizes our text and cards.
        if (!windowTarget_)
        {
            auto props = D2D1::RenderTargetProperties(
                D2D1_RENDER_TARGET_TYPE_SOFTWARE,
                D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
            check(drawing_.factory()->CreateHwndRenderTarget(
                props, D2D1::HwndRenderTargetProperties(h, D2D1::SizeU(w, hgt)), &windowTarget_));
        }
        else if (w != width_ || hgt != height_)
            windowTarget_->Resize(D2D1::SizeU(w, hgt));
        width_ = w;
        height_ = hgt;
        target_ = windowTarget_.Get();
    }
    target_->SetDpi(96 * scale, 96 * scale);
    target_->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
    if (!brush_)
        check(target_->CreateSolidColorBrush(D2D1::ColorF(0, 1), &brush_));
    target_->BeginDraw();
    target_->SetTransform(D2D1::Matrix3x2F::Identity());
    target_->Clear(clear);
    return true;
}
bool Canvas::end()
{
    if (!target_)
        return false;
    auto result = target_->EndDraw();
    if (FAILED(result))
    {
        reset();
        InvalidateRect(hwnd_, nullptr, FALSE);
        return false;
    }
    if (layered_)
    {
        SIZE size{width_, height_};
        POINT source{};
        RECT r{};
        GetWindowRect(hwnd_, &r);
        POINT destination{r.left, r.top};
        BLENDFUNCTION blend{AC_SRC_OVER, 0, 255, AC_SRC_ALPHA};
        return UpdateLayeredWindow(hwnd_, nullptr, &destination, &size, dc_, &source, 0, &blend, ULW_ALPHA) !=
               FALSE;
    }
    return true;
}
void Canvas::text(const std::wstring &s, D2D1_RECT_F r, float size, Color c, bool bold)
{
    brush_->SetColor(c);
    target_->DrawTextW(s.c_str(), (UINT32)s.size(), drawing_.format(size, bold), r, brush_.Get(),
                       D2D1_DRAW_TEXT_OPTIONS_CLIP);
}
void Canvas::rect(D2D1_RECT_F r, Color c, float radius)
{
    brush_->SetColor(c);
    if (radius > 0)
        target_->FillRoundedRectangle(D2D1::RoundedRect(r, radius, radius), brush_.Get());
    else
        target_->FillRectangle(r, brush_.Get());
}
void Canvas::border(D2D1_RECT_F r, Color c, float radius, float width)
{
    brush_->SetColor(c);
    if (radius > 0)
        target_->DrawRoundedRectangle(D2D1::RoundedRect(r, radius, radius), brush_.Get(), width);
    else
        target_->DrawRectangle(r, brush_.Get(), width);
}
void Canvas::line(float x, float y, float x2, float y2, Color c, float width)
{
    brush_->SetColor(c);
    target_->DrawLine({x, y}, {x2, y2}, brush_.Get(), width);
}
void Canvas::progress(D2D1_RECT_F r, std::optional<double> value, Color fill, Color track, float animation)
{
    rect(r, track, 5);
    if (value && *value > 0)
    {
        auto f = r;
        f.right = f.left + (f.right - f.left) * (float)std::clamp(*value / 100, 0., 1.) * animation;
        rect(f, fill, 5);
    }
}
void Canvas::spindle(float cx, float y, float height, float width, Color tint)
{
    ComPtr<ID2D1PathGeometry> path;
    check(drawing_.factory()->CreatePathGeometry(&path));
    ComPtr<ID2D1GeometrySink> sink;
    check(path->Open(&sink));
    sink->BeginFigure({cx, y}, D2D1_FIGURE_BEGIN_FILLED);
    sink->AddBezier({{cx + width * .1f, y + height * .16f},
                     {cx + width * .5f, y + height * .31f},
                     {cx + width * .5f, y + height * .5f}});
    sink->AddBezier(
        {{cx + width * .5f, y + height * .69f}, {cx + width * .1f, y + height * .84f}, {cx, y + height}});
    sink->AddBezier({{cx - width * .1f, y + height * .84f},
                     {cx - width * .5f, y + height * .69f},
                     {cx - width * .5f, y + height * .5f}});
    sink->AddBezier({{cx - width * .5f, y + height * .31f}, {cx - width * .1f, y + height * .16f}, {cx, y}});
    sink->EndFigure(D2D1_FIGURE_END_CLOSED);
    check(sink->Close());
    brush_->SetColor(tint);
    target_->FillGeometry(path.Get(), brush_.Get());
}
void Canvas::clip(D2D1_RECT_F r)
{
    target_->PushAxisAlignedClip(r, D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
}
void Canvas::unclip()
{
    target_->PopAxisAlignedClip();
}
} // namespace cqm
