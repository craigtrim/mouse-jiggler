namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    /// States that an out parameter is not null when the method returns the given value.
    /// </summary>
    /// <remarks>
    /// The .NET Framework 4.8 reference assemblies predate the nullable attributes, so the one
    /// this codebase needs is declared here rather than imported. It is internal on purpose:
    /// it is a compile-time contract, not part of the public surface of this library.
    ///
    /// Issue #3 asks for this to be compiled into every project that applies it, because an
    /// internal type in Core cannot be named from App. Only Core applies it today, so only Core
    /// declares it. A second copy elsewhere would collide with this one inside the test project,
    /// which already sees the internals of two assemblies, and that collision is a warning that
    /// this build treats as an error.
    ///
    /// MemberNotNullAttribute is deliberately absent. It describes a field left unassigned by a
    /// constructor and filled in by a helper, and nothing here is built that way: there is not one
    /// null-forgiving initializer in src. Declaring an attribute nothing applies would be an
    /// unused type that the compiler cannot warn about.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        public NotNullWhenAttribute(bool returnValue)
        {
            ReturnValue = returnValue;
        }

        /// <summary>The return value that makes the parameter non-null.</summary>
        public bool ReturnValue { get; }
    }
}
