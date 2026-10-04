using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;

namespace EchoSub.Desktop;

public sealed partial class MainWindow
{
    private string? translationCommandFailure;
    private readonly TextBlock translationErrorDetails = new() { TextWrapping = TextWrapping.Wrap };

    // Match the worker's numeric loopback endpoint contract before sending a command.
    private static bool IsLocalTranslationEndpoint(string? value)
    {
        if (value is null || value.Length > 256 || !value.StartsWith("http://", StringComparison.Ordinal) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address)) return false;
        var rest = value[7..];
        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? "" : rest[slash..];
        var colon = authority.LastIndexOf(':');
        if (colon < 0) return false;
        var host = authority[..colon];
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        else if (host.Split('.') is not { Length: 4 } octets ||
            octets.Any(octet => octet.Length > 1 && octet[0] == '0' || !byte.TryParse(octet, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _))) return false;
        if (!IPAddress.TryParse(host, out var numericAddress) || !IPAddress.IsLoopback(numericAddress)) return false;
        return (path is "" or "/" or "/v1" or "/v1/") && colon >= 0 &&
            int.TryParse(authority[(colon + 1)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535;
    }

    private static string TranslationFailureAdvice(string failure) => failure switch
    {
        "Connection" => "번역 서버에 연결하지 못했습니다. 런처에서 서버가 준비됐는지 확인한 뒤 서버 연결 / 모델 조회를 누르세요.",
        "Deadline" or "Contract(Deadline)" => "번역 서버의 응답 시간이 초과됐습니다. 모델 준비 상태를 확인한 뒤 다시 시도하세요.",
        "ModelProfileMismatch" => "모델과 고급 번역 설정이 맞지 않습니다. 실행한 모델에 맞는 입력 방식을 선택한 뒤 모델을 적용하세요.",
        "Contract(InvalidModel)" => "사용할 모델을 결정하지 못했습니다. 서버가 제공하는 모델과 선택한 모델을 확인하세요. 목록이 비어 있으면 서버에서 모델 하나를 제공한 뒤 다시 연결하세요.",
        "Contract(InvalidEndpoint)" => "서버 주소를 확인하세요. 예: http://127.0.0.1:1234/v1/ . 이 PC의 숫자 주소와 포트만 사용할 수 있습니다.",
        "INVALID_CONFIG" => "번역 연결 설정을 적용하지 못했습니다. 서버 주소와 고급 번역 설정을 확인하세요.",
        "Status(401)" or "Status(403)" or "InvalidToken" => "번역 서버가 접근을 거부했습니다. 서버와 런처의 인증 설정을 확인하세요.",
        "Status(404)" => "번역 서버의 요청 경로를 찾지 못했습니다. 서버 주소와 호환 API 지원을 확인하세요.",
        "Status(429)" or "Status(503)" => "번역 서버가 지금 요청을 처리하지 못합니다. 서버 상태를 확인하고 잠시 뒤 다시 시도하세요.",
        "Contract(InvalidLanguage)" => "선택한 번역 입력 방식이 요청 언어를 지원하지 않습니다. 언어와 고급 번역 설정을 확인하세요.",
        "Contract(InvalidResponse)" or "Contract(Incomplete)" or "Contract(Refusal)" or "Contract(OversizedResponse)" => "번역 응답을 사용할 수 없습니다. 서버의 모델과 고급 번역 설정을 확인하세요.",
        "BUSY" or "INVALID_STATE" => "현재 상태에서는 번역 설정을 변경할 수 없습니다. 자막을 중지하고 처리 완료 후 다시 시도하세요.",
        _ => "번역 요청이 실패했습니다. 서버 상태를 확인하세요. 오류 상세를 확인한 뒤 다시 시도하세요."
    };

    private void ReportTranslationFailure(Exception error)
    {
        translationCommandFailure = error switch
        {
            WorkerException worker => worker.Code,
            OperationCanceledException or TimeoutException => "Deadline",
            _ => error.Message
        };
        translationErrorDetails.Text = translationCommandFailure[..Math.Min(translationCommandFailure.Length, 512)];
        translationStatus.Text = TranslationFailureAdvice(translationCommandFailure) +
            (error is WorkerException && translationConfigured ? "\n기존 번역 연결은 유지됩니다." : "\n원문 기록은 유지됩니다.");
    }

    private void UpdateTranslationFeedback(JsonElement translator)
    {
        var state = translator.GetProperty("state").GetString();
        var model = translator.GetProperty("model_id").GetString();
        var error = translationCommandFailure ?? translator.GetProperty("last_error").GetString();
        translationErrorDetails.Text = error is null ? "오류가 없습니다." : error[..Math.Min(error.Length, 512)];
        var summary = translator.GetProperty("catalog_pending").GetBoolean()
            ? "번역 서버에 연결해 모델을 확인하고 있습니다."
            : state switch
            {
                "Ready" => $"번역 연결됨 · {model ?? "모델 확인 필요"}",
                "Preparing" => "번역 연결을 준비하고 있습니다.",
                "Failed" => "번역 연결을 준비하지 못했습니다.",
                _ => "번역이 꺼져 있거나 연결되지 않았습니다."
            };
        translationStatus.Text = summary + (error is not null
            ? "\n" + TranslationFailureAdvice(error) + (state == "Ready" ? "\n기존 번역 연결은 유지됩니다." : "\n원문 기록은 유지됩니다.")
            : translator.GetProperty("catalog_pending").GetBoolean() || state == "Preparing"
                ? "\n조회가 끝날 때까지 기다리세요."
            : state == "Ready" ? "\n메인 화면에서 시작 준비 상태를 확인하세요."
            : "\n실시간 런처로 서버를 준비한 뒤 서버 연결 / 모델 조회를 누르세요.");
    }
}
