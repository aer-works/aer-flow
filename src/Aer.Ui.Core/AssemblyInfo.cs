using System.Runtime.CompilerServices;

// M19 Phase 2 (#187): members that were internal-within-Aer.Ui before the split (editor baselines,
// candidate builders, test-only observation points) keep exactly their pre-split visibility — the
// skin and the UI test suite see them; nothing else does. The seam this project enforces is
// "no Avalonia below this line", not a public-API redesign.
[assembly: InternalsVisibleTo("Aer.Ui")]
[assembly: InternalsVisibleTo("Aer.Ui.Tests")]
