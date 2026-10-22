namespace Leaf.Plugins.Pixoo;

public interface IPortraitPackCache
{
    PortraitPack? Current(Guid agentId);
}

public sealed class PortraitFaceRenderer(IPortraitPackCache packs, ProceduralFaceRenderer fallback, CompositeSettings compositeSettings) : IFaceRenderer
{
    public DisplayProgram Render(Guid agentId, FaceState state, string color, int brightness, FaceGesture? gesture = null)
    {
        var pack = packs.Current(agentId);
        return pack is null
            ? fallback.Render(agentId, state, color, brightness, gesture)
            : PortraitLoopRenderer.Render(pack, state, brightness, gesture, compositeSettings);
    }
}

public static class PortraitLoopRenderer
{
    public static DisplayProgram Render(PortraitPack pack, FaceState state, int brightness, FaceGesture? gesture = null,
        CompositeSettings? compositeSettings = null)
    {
        compositeSettings ??= new();
        if (state == FaceState.Dark)
        {
            var dark = new FrameLoop([new RgbFrame(new byte[RgbFrame.ByteCount], 900)]).Validate();
            return new(dark, null, DisplayPlaybackMode.Continuous);
        }

        var clipState = gesture is not null ? FaceState.Resting : state switch
        {
            FaceState.Working => FaceState.Thinking,
            FaceState.Asleep => FaceState.Dreaming,
            _ => state,
        };
        var dim = state == FaceState.Asleep ? .35 : 1d;
        var level = Math.Clamp(brightness, 0, 100) / 100d * dim;
        var still = new FrameLoop([new RgbFrame(Scale(pack.BasePixels, level), 100)]).Validate();
        if (state == FaceState.Resting && gesture is null)
        {
            var idle = pack.IdleVariants().Select(clip => Scale(clip.Loop, level)).ToArray();
            var composites = PortraitCompositeBuilder.BuildResting(still.Frames[0].Pixels, idle, pack.Provenance.SourceHash, compositeSettings);
            return new DisplayProgram(still, composites[0], DisplayPlaybackMode.Continuous,
                composites.Skip(1).Concat(composites.Take(1)).ToArray()).Validate();
        }

        var clip = pack.Primary(clipState);
        if (clip is null) return new(still, null, DisplayPlaybackMode.Continuous);
        var motion = Scale(clip.Loop, level);
        var loop = state switch
        {
            FaceState.Listening or FaceState.Thinking or FaceState.Working => PortraitCompositeBuilder.BuildActive(
                still.Frames[0].Pixels, motion, pack.Provenance.SourceHash, state, compositeSettings),
            FaceState.Dreaming or FaceState.Asleep => PortraitCompositeBuilder.Repeat(motion, TimeSpan.FromSeconds(60)),
            _ => motion,
        };
        return new DisplayProgram(still, loop, gesture is null ? DisplayPlaybackMode.Continuous : DisplayPlaybackMode.OneShot).Validate();
    }

    private static byte[] Scale(byte[] pixels, double factor)
    {
        var result = new byte[pixels.Length];
        for (var index = 0; index < pixels.Length; index++)
            result[index] = (byte)Math.Clamp(Math.Round(pixels[index] * factor), 0, 255);
        return result;
    }

    private static FrameLoop Scale(FrameLoop loop, double factor)
        => new FrameLoop(loop.Frames.Select(frame => new RgbFrame(Scale(frame.Pixels, factor), 100)).ToArray()).Validate();
}
