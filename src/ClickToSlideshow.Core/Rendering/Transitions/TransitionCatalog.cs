using ClickToSlideshow.Core.Model;

namespace ClickToSlideshow.Core.Rendering.Transitions;

/// <summary>The four transitions in spec §4.6, one stateless instance each.</summary>
public static class TransitionCatalog
{
    private static readonly ITransition CutInstance = new CutTransition();
    private static readonly ITransition CrossfadeInstance = new CrossfadeTransition();
    private static readonly ITransition FadeThroughBlackInstance = new FadeThroughBlackTransition();
    private static readonly ITransition PushLeftInstance = new PushTransition(TransitionKind.PushLeft);
    private static readonly ITransition PushRightInstance = new PushTransition(TransitionKind.PushRight);
    private static readonly ITransition PushUpInstance = new PushTransition(TransitionKind.PushUp);
    private static readonly ITransition PushDownInstance = new PushTransition(TransitionKind.PushDown);

    public static ITransition For(TransitionKind kind) => kind switch
    {
        TransitionKind.Cut => CutInstance,
        TransitionKind.Crossfade => CrossfadeInstance,
        TransitionKind.FadeThroughBlack => FadeThroughBlackInstance,
        TransitionKind.PushLeft => PushLeftInstance,
        TransitionKind.PushRight => PushRightInstance,
        TransitionKind.PushUp => PushUpInstance,
        TransitionKind.PushDown => PushDownInstance,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
