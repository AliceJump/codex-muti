using System.Globalization;

namespace CodexPool.Core;

public static class QuotaLabels
{
    public static string Failure(string? code) => code switch
    {
        "quota_unauthorized" => "额度接口认证失败（401）",
        "quota_forbidden" => "额度接口拒绝访问（403）",
        "quota_rate_limited" => "额度接口限流（429）",
        "quota_upstream_unavailable" => "额度上游服务异常",
        "quota_network_unavailable" => "额度网络连接失败",
        "quota_refresh_timeout" => "额度查询超时",
        "invalid_quota_response" => "额度响应格式异常",
        "quota_auth_unavailable" => "额度凭据恢复失败",
        "quota_storage_or_io_failed" => "额度保存或读取失败",
        _ => "额度查询失败"
    };
    public static string Duration(int? seconds) => seconds switch
    {
        null or <= 0 => "额度窗口",
        604800 => "周",
        var value when value % 86400 == 0 => (value.Value / 86400).ToString(CultureInfo.InvariantCulture) + "d",
        var value when value % 3600 == 0 => (value.Value / 3600).ToString(CultureInfo.InvariantCulture) + "h",
        var value when value % 60 == 0 => (value.Value / 60).ToString(CultureInfo.InvariantCulture) + "m",
        var value => value.Value.ToString(CultureInfo.InvariantCulture) + "s"
    };
}
