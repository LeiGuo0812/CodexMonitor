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
    // Background, main text, secondary text, quota, week, credits, raised surface.
    const wchar_t *presets[4][7] = {
        {L"#F6F8FC", L"#182538", L"#526176", L"#2864C7", L"#7653AB", L"#287A65", L"#FFFFFF"},
        {L"#FBF7EF", L"#372D23", L"#70604F", L"#9A611F", L"#4C638C", L"#49785E", L"#FFFEFA"},
        {L"#202429", L"#F4F6F8", L"#BBC4CF", L"#B1C9E2", L"#CBB9E9", L"#9ED3BE", L"#30363E"},
        {L"#101827", L"#F2F6FF", L"#B9C9E1", L"#93BEFF", L"#C6B3FF", L"#8FD6BF", L"#1C2A40"}};
    Palette p;
    p.dark = s.preset == 2 || s.preset == 3;
    p.contrast = highContrast();
    p.glass = transparencyEnabled() && !p.contrast;
    if (s.preset < 4)
    {
        auto &c = presets[s.preset];
        p.background = color(c[0]);
        p.primary = color(c[1]);
        p.secondary = color(c[2]);
        p.accent = color(c[3]);
        p.weekly = color(c[4]);
        p.credit = color(c[5]);
        p.surface = color(c[6]);
    }
    else
    {
        p.background = color(s.background);
        p.primary = color(s.primary);
        p.secondary = color(s.secondary);
        p.accent = color(s.accent);
        p.dark = p.background.r * .2126f + p.background.g * .7152f + p.background.b * .0722f < .5f;
        p.weekly = color(p.dark ? L"#C6B3FF" : L"#7653AB");
        p.credit = color(p.dark ? L"#8FD6BF" : L"#287A65");
        p.surface = blend(p.background, color(L"#FFFFFF"), p.dark ? .06f : .72f);
    }
    p.track = p.dark ? blend(p.background, color(L"#000000"), .24f) : blend(p.surface, p.secondary, .24f);
    if (p.contrast)
    {
        auto sys = [](int index)
        {
            COLORREF c = GetSysColor(index);
            return Color{GetRValue(c) / 255.f, GetGValue(c) / 255.f, GetBValue(c) / 255.f, 1};
        };
        p.background = sys(COLOR_WINDOW);
        p.primary = p.secondary = sys(COLOR_WINDOWTEXT);
        p.accent = p.weekly = p.credit = sys(COLOR_HIGHLIGHT);
        p.surface = p.background;
        p.track = p.secondary;
    }
    return p;
}
float backdropOpacity(const Palette &p, double concentration)
{
    if (!p.glass)
        return 1.f;
    // Keep bright presets bright over a dark desktop, without fading text.
    float amount = (float)std::clamp(concentration, .4, 1.);
    return p.dark ? .55f + .4f * amount : .72f + .26f * amount;
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
    lightboxFace_.Reset();
    lightboxRim_.Reset();
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
void Canvas::lightbox(D2D1_RECT_F r, float amount, bool dark)
{
    if (!target_ || amount <= 0)
        return;
    amount = std::clamp(amount, 0.f, 1.f);
    float fade = amount * amount * (3 - 2 * amount);
    const auto white = D2D1::ColorF(D2D1::ColorF::White);
    // Keep brushes with this render target; hover frames only change their
    // geometry and opacity, without creating blur surfaces or path geometries.
    if (!lightboxFace_)
    {
        D2D1_GRADIENT_STOP stops[] = {{0, alpha(white, 1)}, {.55f, alpha(white, .55f)},
                                     {1, alpha(white, 0)}};
        ComPtr<ID2D1GradientStopCollection> collection;
        check(target_->CreateGradientStopCollection(stops, (UINT32)std::size(stops),
                                                    D2D1_GAMMA_2_2, D2D1_EXTEND_MODE_CLAMP, &collection));
        check(target_->CreateRadialGradientBrush(
            D2D1::RadialGradientBrushProperties({0, 0}, {0, 0}, 1, 1), collection.Get(), &lightboxFace_));
    }
    if (!lightboxRim_)
    {
        D2D1_GRADIENT_STOP stops[] = {{0, alpha(white, 1)}, {.42f, alpha(white, .38f)},
                                     {.65f, alpha(white, .25f)}, {1, alpha(white, .78f)}};
        ComPtr<ID2D1GradientStopCollection> collection;
        check(target_->CreateGradientStopCollection(stops, (UINT32)std::size(stops),
                                                    D2D1_GAMMA_2_2, D2D1_EXTEND_MODE_CLAMP, &collection));
        check(target_->CreateLinearGradientBrush(
            D2D1::LinearGradientBrushProperties({0, 0}, {0, 1}), collection.Get(), &lightboxRim_));
    }
    float width = r.right - r.left, height = r.bottom - r.top;
    auto rounded = D2D1::RoundedRect(r, 6, 6);
    // Wide, low-opacity halos fade into the existing taskbar. All layers add
    // light, including on a light taskbar; no shadow tint darkens the surface.
    for (auto layer : {std::pair{7.f, .018f}, std::pair{4.f, .035f}, std::pair{2.f, .06f}})
    {
        brush_->SetColor(alpha(white, layer.second * fade * (dark ? 1.f : 1.5f)));
        target_->DrawRoundedRectangle(rounded, brush_.Get(), layer.first);
    }
    lightboxFace_->SetCenter({r.left + width * .5f, r.top + height * .48f});
    lightboxFace_->SetRadiusX(width * .65f);
    lightboxFace_->SetRadiusY(height * .85f);
    lightboxFace_->SetOpacity(fade * (dark ? .15f : .34f));
    target_->FillRoundedRectangle(rounded, lightboxFace_.Get());
    lightboxRim_->SetStartPoint({0, r.top});
    lightboxRim_->SetEndPoint({0, r.bottom});
    lightboxRim_->SetOpacity(fade * (dark ? .48f : .7f));
    target_->DrawRoundedRectangle(rounded, lightboxRim_.Get(), .9f);
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
