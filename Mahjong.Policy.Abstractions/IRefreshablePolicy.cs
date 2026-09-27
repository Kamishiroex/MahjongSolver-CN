namespace Mahjong.Policy.Abstractions;

/// <summary>A nonblocking policy whose answer can change without a new game snapshot.</summary>
public interface IRefreshablePolicy : IPolicy
{
    bool RequiresRefresh { get; }
    void Invalidate();
}
