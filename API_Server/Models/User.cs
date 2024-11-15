﻿using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;
using System.Globalization;

namespace API_Server.Models
{
    public class User
    {
        [BsonId]
        public ObjectId Id { get; set; }
        public string? Name { get; set; }
        public string? Username { get; set; }
        public string? Email { get; set; }
        public DateTime DateOfBirth { get; set; }
        public string? Password { get; set; }
        public string? PasswordHash { get; set; }
        public bool IsEmailVerified { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
