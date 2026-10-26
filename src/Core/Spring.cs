// Copyright (c) 2026 Andrey Mantilla Leon
// Licensed under the MIT License. See LICENSE in the project root.

using System;

namespace DynamicIsland.Core;

/// <summary>Resorte amortiguado (masa = 1). Anima todas las medidas de la isla.</summary>
public sealed class Spring
{
    public double Value;
    public double Velocity;
    public double Target;
    public double Stiffness;
    public double Damping;
    public readonly double Epsilon;

    public Spring(double value, double stiffness = 210, double dampingRatio = 0.7, double epsilon = 0.05)
    {
        Value = Target = value;
        Epsilon = epsilon;
        Configure(stiffness, dampingRatio);
    }

    public void Configure(double stiffness, double dampingRatio)
    {
        Stiffness = stiffness;
        Damping = 2 * dampingRatio * Math.Sqrt(stiffness);
    }

    public bool IsAtRest => Math.Abs(Value - Target) < Epsilon && Math.Abs(Velocity) < Epsilon * 10;

    public void Step(double dt)
    {
        double force = -Stiffness * (Value - Target) - Damping * Velocity;
        Velocity += force * dt;
        Value += Velocity * dt;
    }

    public void Snap()
    {
        Value = Target;
        Velocity = 0;
    }
}
