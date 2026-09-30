#pragma once
#include "core.h"
#include <d2d1.h>
#include <dwrite.h>
#include <wrl/client.h>
#include <map>
namespace cqm
{
using Microsoft::WRL::ComPtr;
using Color = D2D1_COLOR_F;
Color color(const std::wstring &hex, float alpha = 1);
Color alpha(Color color, float value);
Color blend(Color a, Color b, float amount);
struct Palette
{
    Color background, primary, secondary, accent, weekly, credit, surface, track;
    bool dark = false, contrast = false, glass = true;
};
Palette palette(const Settings &settings);
float backdropOpacity(const Palette &palette, double concentration);
class Drawing
{
    ComPtr<ID2D1Factory> factory_;
    ComPtr<IDWriteFactory> write_;
    std::map<std::pair<int, bool>, ComPtr<IDWriteTextFormat>> formats_;

  public:
    Drawing();
    ID2D1Factory *factory() const
    {
        return factory_.Get();
    }
    IDWriteFactory *write() const
    {
        return write_.Get();
    }
    IDWriteTextFormat *format(float size, bool bold = false);
    D2D1_SIZE_F measure(const std::wstring &text, float size, bool bold = false, float width = 10000);
};
class Canvas
{
    Drawing &drawing_;
    ComPtr<ID2D1HwndRenderTarget> windowTarget_;
    ComPtr<ID2D1DCRenderTarget> dcTarget_;
    ComPtr<ID2D1SolidColorBrush> brush_;
    ID2D1RenderTarget *target_ = nullptr;
    HWND hwnd_ = nullptr;
    HDC dc_ = nullptr;
    HBITMAP bitmap_ = nullptr;
    HGDIOBJ previous_ = nullptr;
    int width_ = 0, height_ = 0;
    bool layered_ = false;

  public:
    explicit Canvas(Drawing &drawing) : drawing_(drawing)
    {
    }
    ~Canvas();
    Canvas(const Canvas &) = delete;
    Canvas &operator=(const Canvas &) = delete;
    bool begin(HWND hwnd, bool layered, float scale, Color clear);
    bool end();
    void reset();
    void text(const std::wstring &value, D2D1_RECT_F rect, float size, Color color, bool bold = false);
    void rect(D2D1_RECT_F rect, Color color, float radius = 0);
    void border(D2D1_RECT_F rect, Color color, float radius = 0, float width = 1);
    void line(float x, float y, float x2, float y2, Color color, float width = 1);
    void progress(D2D1_RECT_F rect, std::optional<double> value, Color fill, Color track,
                  float animation = 1);
    void spindle(float center, float top, float height, float width, Color tint);
    void clip(D2D1_RECT_F r);
    void unclip();
};
} // namespace cqm
