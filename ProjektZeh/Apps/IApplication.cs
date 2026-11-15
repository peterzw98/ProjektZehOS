using ProjektZeh.Graphics;
using ProjektZeh.Input;

namespace ProjektZeh.Apps;

internal interface IApplication
{
    void Start();
    void Handle(InputEvent value);
    void Update(long milliseconds);
    void Resize(int width, int height) { }
    void Draw(PixelSurface surface, int x, int y, int width, int height, bool active);
    bool IsCloseRequested => false;
    /// <summary>true, solange die Anwendung als aktives Fenster fortlaufend neue Bilder liefert.</summary>
    bool NeedsAnimation => false;
    /// <summary>true, solange die Anwendung den Zeiger eingefangen hat; dann erhaelt sie alle Zeigerereignisse.</summary>
    bool CapturesPointer => false;
    bool RequestClose() => true;
    void Close();
}
