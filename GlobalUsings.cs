// WPF and WinForms are both referenced (WinForms only for the tray icon and
// Screen), and ImplicitUsings pulls both namespaces in. Five type names exist in
// both, so every file that touched one had to alias it locally — three separate
// build failures, each fixed in a different file, all the same mistake.
//
// These five are always the WPF ones in this codebase. Names that genuinely go
// both ways — Brush, Color, Point — are deliberately NOT here: TrayController
// draws its icon with System.Drawing and must keep those.

global using CheckBox = System.Windows.Controls.CheckBox;
global using ComboBox = System.Windows.Controls.ComboBox;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using MessageBox = System.Windows.MessageBox;
global using TextBox = System.Windows.Controls.TextBox;
