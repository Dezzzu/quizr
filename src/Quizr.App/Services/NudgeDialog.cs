using Quizr.Domain;

namespace Quizr.App.Services;

// Nudge is a single-screen multi-select: everyone missing from the game starts selected,
// toggle buttons flip membership, Send @-mentions whoever's still selected. No Step needed —
// there's only one.
internal sealed record NudgeDialogData(GameId GameId, List<long> SelectedPlayerIds);
