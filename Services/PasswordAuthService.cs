using Microsoft.Extensions.Configuration;

namespace SDSChat.Services;

public interface IPasswordAuthService
{
    bool ValidatePassword(string password);
}

public class PasswordAuthService : IPasswordAuthService
{
    private readonly IConfiguration _configuration;

    public PasswordAuthService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool ValidatePassword(string password)
    {
        var correctPassword = _configuration["AppPassword"];
        return !string.IsNullOrEmpty(correctPassword) && password == correctPassword;
    }
}

