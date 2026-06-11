namespace Sas.Auth;

public interface IResponseTemplate
{
    object BuildAllow(AuthResult result);
    object BuildDeny();
}
