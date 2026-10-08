using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace HR.Web.Infrastructure;

/// <summary>Success/error messages shown by the _Toasts partial after a redirect.</summary>
public static class TempDataToastExtensions
{
    public const string SuccessKey = "Toast.Success";
    public const string ErrorKey = "Toast.Error";

    public static void ToastSuccess(this ITempDataDictionary tempData, string message) => tempData[SuccessKey] = message;

    public static void ToastError(this ITempDataDictionary tempData, string message) => tempData[ErrorKey] = message;
}
