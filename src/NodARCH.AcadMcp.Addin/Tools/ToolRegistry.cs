namespace NodARCH.AcadMcp.Addin.Tools;

/// <summary>Single place every tool module is wired in from.</summary>
internal static class ToolRegistry
{
    private static bool _registered;

    /// <summary>
    /// Every module goes through its own lambda, never a direct call or a method group. The JIT
    /// loads each class a method calls into when it compiles that method, so with direct calls a
    /// single module class that cannot load failed this whole method, and with it every tool,
    /// the commands and the ribbon. That happened on vanilla AutoCAD, where one ACA module named
    /// an ACA type the runtime could not find. A lambda is compiled on its own, inside the
    /// guard, so a module that cannot load costs only its own tools and is reported.
    /// </summary>
    internal static void RegisterAll()
    {
        if (_registered) return;
        _registered = true;

        // --- plain AutoCAD ---------------------------------------------------
        Module("SessionTools", () => SessionTools.Register());
        Module("EntityTools", () => EntityTools.Register());
        Module("BlockTools", () => BlockTools.Register());
        Module("AnnotationTools", () => AnnotationTools.Register());
        Module("LayoutTools", () => LayoutTools.Register());
        Module("SheetTools", () => SheetTools.Register());
        Module("PlotTools", () => PlotTools.Register());
        Module("EscapeTools", () => EscapeTools.Register());
        Module("SolidTools", () => SolidTools.Register());
        Module("CSharpBridge", () => CSharpBridge.Register());
        Module("WorkflowTools", () => WorkflowTools.Register());
        Module("DraftingTools", () => DraftingTools.Register());

        // --- AutoCAD Architecture -------------------------------------------
        // These self-gate: they register unconditionally so they always appear in
        // the tool list, and return "aca_unavailable" when ACA is not loaded.
        Module("AcaTools", () => AcaTools.Register());
        Module("AcaOpeningTools", () => AcaOpeningTools.Register());
        Module("AcaSpaceTools", () => AcaSpaceTools.Register());
        Module("AcaSlabRoofTools", () => AcaSlabRoofTools.Register());
        Module("AcaMassTools", () => AcaMassTools.Register());
        Module("AcaGridStructureTools", () => AcaGridStructureTools.Register());
        Module("AcaAssemblyTools", () => AcaAssemblyTools.Register());
        Module("AcaSectionTools", () => AcaSectionTools.Register());
        Module("AcaFabricationTools", () => AcaFabricationTools.Register());
        Module("AcaProjectTools", () => AcaProjectTools.Register());

        // run_lisp: the [LispFunction] callback implementation. Application.Invoke cannot
        // reach AutoLISP at all — it only dispatches to ObjectARX functions registered via
        // acedDefun.
        Module("LispBridge", () => LispBridge.Register());
    }

    private static void Module(string name, Action register) => AcadMcpApp.Guard(name, register);
}
