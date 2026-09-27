namespace Mahjong.Policy.Abstractions;

/// <summary>
/// The policy validates its own current public-table evidence. It must not be scored or
/// rejected using the legacy reader's inferred meld inventory. Schema/read failure gates
/// still apply, and the policy must refuse incomplete or conflicting own-hand evidence.
/// </summary>
public interface IIndependentPublicStatePolicy : IRefreshablePolicy { }
