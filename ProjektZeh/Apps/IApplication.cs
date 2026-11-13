using ProjektZeh.Graphics;
using ProjektZeh.Input;

namespace ProjektZeh.Apps;

internal interface IApplication
{
    void Start();
    void Handle(InputEvent value);
    void Update(long milliseconds);
    void Resize(int width, int height);
    void Draw(PixelSurface surface, int x, int y, int width, int height, bool active);
    void Close();
}
