namespace Leaf.Plugins.Pixoo;

public interface IPortraitPackCache
{
    PortraitPack? Current(Guid agentId);
}

public sealed class PortraitFaceRenderer(IPortraitPackCache packs, ProceduralFaceRenderer fallback) : IFaceRenderer
{
    public FrameLoop Render(Guid agentId, FaceState state, string color, int brightness, FaceGesture? gesture = null)
    {
        var pack = packs.Current(agentId);
        return pack is null
            ? fallback.Render(agentId, state, color, brightness, gesture)
            : PortraitLoopRenderer.Render(pack, state, brightness, gesture);
    }
}

public static class PortraitLoopRenderer
{
    public static FrameLoop Render(PortraitPack pack, FaceState state, int brightness, FaceGesture? gesture = null)
    {
        if (state == FaceState.Dark)
            return new FrameLoop([new RgbFrame(new byte[RgbFrame.ByteCount], 900)]);

        var clipState = gesture is not null ? FaceState.Resting : state switch
        {
            FaceState.Working => FaceState.Thinking,
            FaceState.Asleep => FaceState.Dreaming,
            _ => state,
        };
        var dim = state == FaceState.Asleep ? .35 : 1d;
        var level = Math.Clamp(brightness, 0, 100) / 100d * dim;
        if (!pack.Clips.TryGetValue(clipState, out var clip))
            return new FrameLoop([new RgbFrame(Scale(pack.BasePixels, level), 1000)]).Validate();
        return new FrameLoop(clip.Loop.Frames
            .Select(frame => new RgbFrame(Scale(frame.Pixels, level), frame.DurationMs))
            .ToArray()).Validate();
    }

    private static byte[] Scale(byte[] pixels, double factor)
    {
        var result = new byte[pixels.Length];
        for (var index = 0; index < pixels.Length; index++)
            result[index] = (byte)Math.Clamp(Math.Round(pixels[index] * factor), 0, 255);
        return result;
    }
}
