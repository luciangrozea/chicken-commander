// WPF projects don't get System.IO as an implicit using (it would clash with System.Windows.Shapes.Path in XAML code).
global using System.IO;
global using Path = System.IO.Path;
