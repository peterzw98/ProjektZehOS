using Cosmos.Kernel.System.Graphics.Fonts;

namespace ProjektZeh.Graphics;

internal sealed class CosmosGermanFont : Font
{
    private static CosmosGermanFont? instance;
    public static CosmosGermanFont Default => instance ??= new(PCScreenFont.DefaultFont);

    private CosmosGermanFont(PCScreenFont source)
        : base(source.Width, source.Height, GermanFont.Normalize(source.Data, source.Width, source.Height)) { }
}
