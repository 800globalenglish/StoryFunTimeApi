using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
namespace StoryFunTimeApi.Services;
public class PhotoFilterService
{
    public async Task<byte[]> ApplyCartoonFilter(byte[] imageBytes)
    {
        using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(imageBytes);

        // Build bold ink-line edges from a HEAVILY pre-blurred grayscale copy.
        // Heavy blur first = only strong real edges survive (face outline, eyes,
        // nose, mouth, hairline) instead of skin texture/noise turning into edges.
        using var edges = image.Clone(ctx => ctx
            .Grayscale()
            .GaussianBlur(3.5f)
            .DetectEdges()
            .Contrast(2.0f)   // push detected edges toward pure black/white (bolder lines)
            .Invert()         // background -> white, edges -> black ink lines
        );

        // Flatten the base image into bold, cartoon-style flat color regions.
        image.Mutate(ctx => ctx
            .GaussianBlur(1.0f)   // light smoothing so color regions are cleaner
            .Saturate(1.7f)
            .Contrast(1.25f)
        );
        var quantizer = new WuQuantizer(new QuantizerOptions { MaxColors = 14 });
        image.Mutate(ctx => ctx.Quantize(quantizer));

        // Lay the bold ink lines on top at strong (but not 100%) opacity.
        image.Mutate(ctx => ctx.DrawImage(edges, PixelColorBlendingMode.Multiply, 0.75f));

        using var ms = new MemoryStream();
        await image.SaveAsJpegAsync(ms);
        return ms.ToArray();
    }
}
