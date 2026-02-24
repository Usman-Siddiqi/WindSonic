namespace WindSound.App.Models;

public sealed record PlayerSnapshot(
    bool HasMedia,
    bool IsPlaying,
    bool IsPaused,
    TimeSpan Position,
    TimeSpan Duration);
