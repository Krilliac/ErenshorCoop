using System;

namespace ErenshorDedicatedServer.Data
{
    /// <summary>
    /// Lightweight Vector3 replacement for standalone server (no Unity dependency).
    /// </summary>
    public struct Vec3 : IEquatable<Vec3>
    {
        public float X;
        public float Y;
        public float Z;

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 One = new Vec3(1, 1, 1);
        public static readonly Vec3 Up = new Vec3(0, 1, 0);
        public static readonly Vec3 Forward = new Vec3(0, 0, 1);

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float SqrMagnitude => X * X + Y * Y + Z * Z;
        public float Magnitude => MathF.Sqrt(SqrMagnitude);

        public Vec3 Normalized
        {
            get
            {
                var mag = Magnitude;
                if (mag < 1e-6f) return Zero;
                return new Vec3(X / mag, Y / mag, Z / mag);
            }
        }

        public static float Distance(Vec3 a, Vec3 b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static float DistanceSqr(Vec3 a, Vec3 b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        public static Vec3 Lerp(Vec3 a, Vec3 b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return new Vec3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t
            );
        }

        public static Vec3 MoveTowards(Vec3 current, Vec3 target, float maxDelta)
        {
            var diff = target - current;
            var dist = diff.Magnitude;
            if (dist <= maxDelta || dist < 1e-6f)
                return target;
            return current + diff / dist * maxDelta;
        }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator /(Vec3 a, float s) => new Vec3(a.X / s, a.Y / s, a.Z / s);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static bool operator ==(Vec3 a, Vec3 b) => a.Equals(b);
        public static bool operator !=(Vec3 a, Vec3 b) => !a.Equals(b);

        public bool Equals(Vec3 other)
        {
            return MathF.Abs(X - other.X) < 1e-5f
                && MathF.Abs(Y - other.Y) < 1e-5f
                && MathF.Abs(Z - other.Z) < 1e-5f;
        }

        public override bool Equals(object obj) => obj is Vec3 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";
    }

    /// <summary>
    /// Lightweight Quaternion replacement for standalone server (no Unity dependency).
    /// </summary>
    public struct Quat : IEquatable<Quat>
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public static readonly Quat Identity = new Quat(0, 0, 0, 1);

        public Quat(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public static Quat Euler(float yawDegrees)
        {
            var halfRad = yawDegrees * MathF.PI / 360f;
            return new Quat(0, MathF.Sin(halfRad), 0, MathF.Cos(halfRad));
        }

        public Vec3 Forward
        {
            get
            {
                return new Vec3(
                    2f * (X * Z + W * Y),
                    2f * (Y * Z - W * X),
                    1f - 2f * (X * X + Y * Y)
                );
            }
        }

        public bool Equals(Quat other)
        {
            return MathF.Abs(X - other.X) < 1e-5f
                && MathF.Abs(Y - other.Y) < 1e-5f
                && MathF.Abs(Z - other.Z) < 1e-5f
                && MathF.Abs(W - other.W) < 1e-5f;
        }

        public override bool Equals(object obj) => obj is Quat other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z, W);
        public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3}, {W:F3})";
    }
}
