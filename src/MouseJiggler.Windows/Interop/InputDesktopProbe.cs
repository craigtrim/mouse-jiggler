using MouseJiggler.Core.Abstractions;

namespace MouseJiggler.Windows.Interop
{
    /// <summary>
    /// The live probe: asks Windows which desktop is receiving input, every time it is called.
    /// </summary>
    /// <remarks>
    /// The answer is never cached. The secure desktop appears and disappears between one
    /// evaluation and the next, and a remembered "yes" would send input at a UAC prompt.
    /// </remarks>
    public sealed class InputDesktopProbe : IInputDesktopProbe
    {
        public bool IsInputDesktopAvailable()
        {
            return DesktopAccess.IsInputDesktopAvailable();
        }
    }
}
