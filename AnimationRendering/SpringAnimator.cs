using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace SkyBar.AnimationRendering;

/// <summary>
/// Preset spring yang meniru kurva animasi macOS / SwiftUI.
/// Response = durasi kira-kira (detik), DampingFraction = 1 berarti tanpa pantulan.
/// </summary>
public static class SpringPresets
{
    /// <summary>Ekspansi notch: kenyal halus seperti Dynamic Island.</summary>
    public static (double Response, double Damping) Expand => (0.46, 0.78);

    /// <summary>Kolaps notch: cepat, hampir tanpa overshoot.</summary>
    public static (double Response, double Damping) Collapse => (0.38, 0.92);

    /// <summary>Konten fade/scale.</summary>
    public static (double Response, double Damping) Content => (0.30, 0.95);

    /// <summary>Mengikuti kursor (magnifikasi dock) tanpa pantulan.</summary>
    public static (double Response, double Damping) Interactive => (0.20, 1.0);

    /// <summary>Kembali ke posisi semula setelah interaksi selesai.</summary>
    public static (double Response, double Damping) Settle => (0.34, 0.90);
}

/// <summary>
/// Nilai yang dianimasikan dengan fisika pegas (damped harmonic oscillator).
/// Berjalan setiap frame layar (mengikuti refresh rate monitor, bukan dikunci 60 FPS),
/// dan mempertahankan kecepatan saat target diubah di tengah animasi sehingga
/// transisi selalu mulus seperti di macOS.
/// </summary>
public sealed class SpringValue
{
    private double _value;

    public SpringValue(double initialValue, double response = 0.42, double dampingFraction = 0.85)
    {
        _value = initialValue;
        Target = initialValue;
        Response = response;
        DampingFraction = dampingFraction;
    }

    public double Value => _value;
    public double Target { get; private set; }
    public double Velocity { get; private set; }
    public double Response { get; set; }
    public double DampingFraction { get; set; }

    /// <summary>Selisih posisi minimum sebelum dianggap diam.</summary>
    public double RestDisplacement { get; set; } = 0.002;

    /// <summary>Kecepatan minimum sebelum dianggap diam.</summary>
    public double RestVelocity { get; set; } = 0.02;

    public bool IsAnimating { get; private set; }

    public event Action<double>? ValueChanged;

    public void AnimateTo(double target, (double Response, double Damping) preset)
        => AnimateTo(target, preset.Response, preset.Damping);

    public void AnimateTo(double target) => AnimateTo(target, Response, DampingFraction);

    public void AnimateTo(double target, double response, double dampingFraction)
    {
        Response = response;
        DampingFraction = dampingFraction;
        Target = target;

        if (!SpringAnimator.AnimationsEnabled)
        {
            SnapTo(target);
            return;
        }

        if (!IsAnimating &&
            Math.Abs(_value - target) <= RestDisplacement &&
            Math.Abs(Velocity) <= RestVelocity)
        {
            SetValue(target);
            return;
        }

        if (!IsAnimating)
        {
            IsAnimating = true;
            SpringClock.Register(this);
        }
    }

    /// <summary>Langsung melompat ke nilai tertentu tanpa animasi.</summary>
    public void SnapTo(double value)
    {
        IsAnimating = false;
        Velocity = 0;
        Target = value;
        SetValue(value);
    }

    /// <summary>Memberi dorongan kecepatan (misal untuk efek "bump").</summary>
    public void AddVelocity(double velocity)
    {
        if (!SpringAnimator.AnimationsEnabled) return;
        Velocity += velocity;
        if (!IsAnimating)
        {
            IsAnimating = true;
            SpringClock.Register(this);
        }
    }

    internal void Step(double dt)
    {
        double omega = 2.0 * Math.PI / Math.Max(Response, 0.01);
        double stiffness = omega * omega;
        double damping = 2.0 * DampingFraction * omega;

        double x = _value;
        double v = Velocity;
        double remaining = dt;

        // Sub-step kecil (semi-implicit Euler) agar stabil di refresh rate berapa pun.
        const double maxStep = 1.0 / 480.0;
        while (remaining > 1e-9)
        {
            double h = Math.Min(maxStep, remaining);
            double acceleration = -stiffness * (x - Target) - damping * v;
            v += acceleration * h;
            x += v * h;
            remaining -= h;
        }

        Velocity = v;

        if (Math.Abs(x - Target) <= RestDisplacement && Math.Abs(v) <= RestVelocity)
        {
            IsAnimating = false;
            Velocity = 0;
            x = Target;
        }

        SetValue(x);
    }

    private void SetValue(double value)
    {
        if (_value.Equals(value)) return;
        _value = value;
        ValueChanged?.Invoke(value);
    }
}

/// <summary>
/// Jam animasi global yang terhubung ke <see cref="CompositionTarget.Rendering"/>.
/// Hanya aktif saat ada pegas yang bergerak, sehingga tidak memakan CPU saat diam.
/// </summary>
internal static class SpringClock
{
    private static readonly List<SpringValue> Active = new();
    private static bool _isHooked;
    private static TimeSpan _lastRenderTime;

    public static void Register(SpringValue spring)
    {
        if (!Active.Contains(spring)) Active.Add(spring);
        if (_isHooked) return;

        _isHooked = true;
        _lastRenderTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    public static void SnapAll()
    {
        foreach (var spring in Active.ToArray()) spring.SnapTo(spring.Target);
        Active.Clear();
        Unhook();
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        TimeSpan now = e is RenderingEventArgs args ? args.RenderingTime : TimeSpan.FromTicks(DateTime.Now.Ticks);

        // Rendering bisa terpanggil lebih dari sekali per frame; abaikan duplikat.
        if (now == _lastRenderTime) return;

        double dt = _lastRenderTime == TimeSpan.Zero ? 1.0 / 60.0 : (now - _lastRenderTime).TotalSeconds;
        _lastRenderTime = now;
        dt = Math.Clamp(dt, 0.0, 1.0 / 24.0);

        foreach (var spring in Active.ToArray())
        {
            if (spring.IsAnimating) spring.Step(dt);
        }

        Active.RemoveAll(s => !s.IsAnimating);
        if (Active.Count == 0) Unhook();
    }

    private static void Unhook()
    {
        if (!_isHooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _isHooked = false;
    }
}

public static class SpringAnimator
{
    private static bool _animationsEnabled = true;
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, SpringValue>> PropertySprings = new();

    public static bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set
        {
            _animationsEnabled = value;
            if (!value) SpringClock.SnapAll();
        }
    }

    /// <summary>
    /// Menganimasikan DependencyProperty bertipe double dengan pegas fisika.
    /// Pemanggilan ulang di tengah animasi akan meneruskan kecepatan yang ada.
    /// </summary>
    public static void Animate(
        DependencyObject target,
        DependencyProperty property,
        double toValue,
        double response = 0.42,
        double dampingFraction = 0.85)
    {
        if (target is UIElement element) element.BeginAnimation(property, null);

        var map = PropertySprings.GetOrCreateValue(target);
        if (!map.TryGetValue(property, out var spring))
        {
            double current = target.GetValue(property) is double d && !double.IsNaN(d) ? d : toValue;
            spring = new SpringValue(current, response, dampingFraction);
            spring.ValueChanged += v => target.SetValue(property, v);
            map[property] = spring;
        }

        spring.AnimateTo(toValue, response, dampingFraction);
    }
}
