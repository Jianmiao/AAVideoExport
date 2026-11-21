namespace AAVideoExport.Core;

public static class ExportBuildProfile
{
#if AAVE_DEVELOPMENT_AUDIT
    public static bool SinglePassEnabled => false;
#else
    public static bool SinglePassEnabled => true;
#endif
#if AAVE_DEVELOPMENT_AUDIT
    public static bool DevelopmentAuditEnabled => true;
#else
    public static bool DevelopmentAuditEnabled => false;
#endif
}
