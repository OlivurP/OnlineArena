namespace Shared.Protocol;

/// <summary>
/// Bevægelsesregler deles af server (autoritativ simulation) og klient (client-side prediction).
/// Når begge kører samme deterministiske kode, bliver korrektioner ved reconciliation små.
/// </summary>
public static class GameRules
{
    public const float WorldWidth = 40f;
    public const float WorldHeight = 20f;
    public const float SpeedPerInput = 0.5f;
    public const float PickupRadius = 1f;

    public static (float X, float Y) Move(float x, float y, float dx, float dy)
    {
        // Validering: ugyldigt input behandles som "stå stille" (don't trust the client)
        if (!float.IsFinite(dx)) dx = 0;
        if (!float.IsFinite(dy)) dy = 0;

        dx = Math.Clamp(dx, -1f, 1f);
        dy = Math.Clamp(dy, -1f, 1f);

        // Diagonal bevægelse må ikke være hurtigere end lige
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length > 1f)
        {
            dx /= length;
            dy /= length;
        }

        return (
            Math.Clamp(x + dx * SpeedPerInput, 0f, WorldWidth),
            Math.Clamp(y + dy * SpeedPerInput, 0f, WorldHeight));
    }
}
