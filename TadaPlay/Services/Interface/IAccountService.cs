
using TadaPlay.Common.Models;

namespace TadaPlay.Services.Interface;

public interface IAccountService
{
    Task<List<User>> GetAllAsync();

    Task<bool> DoAuthAsync();

    Task<bool> DoLoginAsync(string Username, string Password, bool AutoLogin);

    Task<bool> UpdateUserInfo(string full_name, string nick_name, string current_password, string new_password);

    Task<bool> UpdateCurrentIPToServer(string currentIp);
}