﻿using API_Server.Models;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using API_Server.Services;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.JsonPatch;

namespace API_Server.Controllers
{
    [Route("api/user")]
    [ApiController]
    public class UserController : ControllerBase
    {
        private readonly MongoDbService _context;
        private readonly EmailService _emailService;
        private readonly JwtService _jwtService;
        public UserController(MongoDbService context, EmailService emailService, JwtService jwtService)
        {
            _context = context;
            _emailService = emailService;
            _jwtService = jwtService;
        }

        private static readonly ConcurrentDictionary<string, User> _users = new ConcurrentDictionary<string, User>();
        private static string? currentEmail;
        private static string? curentOtp;

        //API cho Registration
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] Register registerModel)
        {
            //Kiểm tra 2 mật khẩu được nhập vào
            if (registerModel.Password != registerModel.ConfirmPassword)
            {
                return BadRequest("Mật khẩu xác nhận không hợp lệ.");
            }
            //Kiểm tra xem tên người dùng hoặc mail đã tồn tại hay chưa
            var filter = Builders<User>.Filter.Or(
                Builders<User>.Filter.Eq(u => u.Username, registerModel.Username),
                Builders<User>.Filter.Eq(u => u.Email, registerModel.Email)
            );
            //Luu kết quả tìm trong database 
            var existingUser = await _context.Users.Find(filter).FirstOrDefaultAsync();

            if (existingUser != null)
            {
                return BadRequest("Tên người dùng hoặc email đã tồn tại.");
            }

            //Tạo mã otp và gắn cho biến tạm để dùng so sánh trong hàm xác minh otp
            var otp = new Random().Next(000000, 999999).ToString();
            curentOtp = otp;
            //Hash password bằng framework
            var hashedPassword = BCrypt.Net.BCrypt.HashPassword(registerModel.Password);
            //Tạo user nhận dữ liệu được vào
            var user = new User
            {
                Name = registerModel.Name,
                Username = registerModel.Username,
                Password = registerModel.Password,
                PasswordHash = hashedPassword,
                DateOfBirth = registerModel.DateOfBirth,
                Email = registerModel.Email,
                CreatedAt = DateTime.UtcNow
            };

            _users[registerModel.Email] = user;
            //Gửi otp với mail được nhập
            currentEmail = user.Email;
            _emailService.SendOtpEmail(user.Email, otp);

            return Ok("Đăng kí thành công. Kiếm tra mã OTP trong email của bạn.");
        }
        //API cho Verify Otp
        [HttpPost("Verify-Otp")]
        public async Task<IActionResult> VerifyOtp([FromBody] Otp otpModel)
        {
            //Kiểm tra xem email đã được dùng để đăng kí chưa
            if (string.IsNullOrEmpty(currentEmail))
                return BadRequest("Email này chưa được dùng để đăng kí.");
            //Lưu email đã có từ api registration
            var tempUser = _users[currentEmail];
            //So sánh OTP
            if (curentOtp != otpModel.OTP)
            {
                return BadRequest("OTP không hợp lệ");
            }
            //Kiểm tra xem email đã đăng kí cho user nào chưa
            //Có thể xem xét bỏ đoạn này
            var filter = Builders<User>.Filter.Eq(x => x.Email, tempUser.Email);
            var existingUser = await _context.Users.Find(filter).FirstOrDefaultAsync();

            if (existingUser != null)
            {
                return BadRequest("Người dùng đã được đăng kí.");
            }
            //Tạo user để thêm dữ liệu vào database
            var newUser = new User
            {
                Name = tempUser.Username,
                Username = tempUser.Username,
                PasswordHash = tempUser.PasswordHash,
                DateOfBirth = tempUser.DateOfBirth,
                Email = tempUser.Email,
                CreatedAt = DateTime.UtcNow,
                IsEmailVerified = true
            };
            newUser.IsEmailVerified = true;
            await _context.Users.InsertOneAsync(newUser);
            return Ok("Đăng kí thành công. Email của bạn đã được xác nhận.");
        }
        //API cho Login
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] Login loginModel)
        {
            if (loginModel == null || string.IsNullOrEmpty(loginModel.Username) || string.IsNullOrEmpty(loginModel.Password))
            {
                return BadRequest("Yêu cầu đăng nhập thất bại.");
            }

            var user = await _context.Users.Find(u => u.Username == loginModel.Username).FirstOrDefaultAsync();

            if (user == null || string.IsNullOrEmpty(user.PasswordHash))
            {
                return Unauthorized("Tên người dùng không hợp lệ.");
            }

            if (!BCrypt.Net.BCrypt.Verify(loginModel.Password, user.PasswordHash))
            {
                return Unauthorized("Mật khẩu không hợp lệ.");
            }

            var accessToken = _jwtService.GenerateAccessToken(user);
            var refreshToken = _jwtService.GenerateRefreshToken();
            var jti = _jwtService.GetJtiFromAccessToken(accessToken);
            // Lưu refresh token vào database
            var tokenData = new TokenData
            {
                RefreshToken = refreshToken,
                RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7),
                Username = user.Username,
                Jti = jti
            };

            await _context.Tokens.InsertOneAsync(tokenData);
            //Lưu access token vào cookies
            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Expires = DateTime.UtcNow.AddDays(7),
                Secure = true,
                SameSite = SameSiteMode.Strict
            };
            Response.Cookies.Append("accessToken", accessToken, cookieOptions);
            //Trả về refresh token và access token
            return Ok(new
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken
            });
        }
        //API cho logout
        [HttpPost("logout")]
        [Authorize]
        public async Task<IActionResult> Logout()
        {
            // Lấy access token từ cookies
            if (!Request.Cookies.TryGetValue("accessToken", out var accessToken))
            {
                return BadRequest("Yêu cầu không hợp lệ.");
            }

            // Xác minh token và lấy username
            var claimsPrincipal = _jwtService.ValidateToken(accessToken);//Trả về giá trị người dùng của token
            if (claimsPrincipal == null)
            {
                return Unauthorized("Access token không hợp lệ 1.");
            }

            var usernameClaim = claimsPrincipal.FindFirst("userName");//Tìm username của token
            if (usernameClaim == null || string.IsNullOrEmpty(usernameClaim.Value))
            {
                return Unauthorized("Access token không hợp lệ 2");
            }
            var username = usernameClaim.Value;

            // Tìm user trong database
            var user = await _context.Users.Find(u => u.Username == username).FirstOrDefaultAsync();
            if (user == null)
            {
                return NotFound("Không tìm thấy người dùng.");
            }

            // Xóa refresh token từ database
            var filter = Builders<TokenData>.Filter.Eq(t => t.Username, username);
            await _context.Tokens.DeleteManyAsync(filter);

            // Xóa access token từ cookies
            if (Request.Cookies.ContainsKey("accessToken"))
            {
                Response.Cookies.Delete("accessToken");
            }

            return Ok("Đăng xuất thành công.");
        }
        //Lấy data của người dùng
        [HttpGet("{username}")]
        [Authorize]
        public async Task<IActionResult> GetUser(string username)
        {
            // Lấy access token từ cookies
            if (!Request.Cookies.TryGetValue("accessToken", out var accessToken))
            {
                return BadRequest("Yêu cầu không hợp lệ.");
            }

            // Xác minh token và lấy username
            var claimsPrincipal = _jwtService.ValidateToken(accessToken);//Trả về giá trị người dùng của token

            if (claimsPrincipal == null)
            {
                return Unauthorized("Access token không hợp lệ.");
            }

            //Tìm thông tin người dùng
            var user = await _context.Users.Find(u => u.Username == username).FirstOrDefaultAsync();

            if (user == null)
            {
                return NotFound("Không tìm thấy người dùng.");
            }

            return Ok(user);
        }
        //Xóa người dùng
        [HttpDelete("{username}")]
        [Authorize]
        public async Task<IActionResult> DeleteUser(string username)
        {
            // Lấy access token từ cookies
            if (!Request.Cookies.TryGetValue("accessToken", out var accessToken))
            {
                return BadRequest("Yêu cầu không hợp lệ.");
            }

            // Xác minh token và lấy username
            var claimsPrincipal = _jwtService.ValidateToken(accessToken);//Trả về giá trị người dùng của token
            if (claimsPrincipal == null)
            {
                return Unauthorized("Access token không hợp lệ 1.");
            }

            //Tìm thông tin người dùng
            var user = await _context.Users.Find(u => u.Username == username).FirstOrDefaultAsync();

            if (user == null)
            {
                return NotFound("Không tìm thấy người dùng.");
            }
            //Xóa người dùng
            await _context.Users.DeleteOneAsync(u => u.Username == username);

            return Ok("Xóa người dùng thành công.");
        }
        //Chỉnh sửa thông tin người dùng
        [HttpPatch("{username}")]
        [Authorize]
        public async Task<IActionResult> UpdateUser(string username, [FromBody] JsonPatchDocument<User> patchDoc)
        {
            var user = await _context.Users.Find(u => u.Username == username).FirstOrDefaultAsync();
            var filter = Builders<User>.Filter.Eq(u => u.Username, username);

            if (user == null)
            {
                return NotFound("Không tìm thấy người dùng.");
            }

            patchDoc.ApplyTo(user, ModelState);
            ModelState.Remove("password");

            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            await _context.Users.ReplaceOneAsync(filter, user);

            return Ok(user);
        }
    }
}
