namespace CodexPool.Floating;

// Exact critically damped motion: retargeting preserves both pose and velocity.
internal sealed class MorphSpring(params double[] initial)
{
    internal double[] Value { get; private set; } = initial.ToArray();
    internal double[] Velocity { get; } = new double[initial.Length];
    internal double[] Target { get; private set; } = initial.ToArray();
    internal void Aim(params double[] target) => Target = target.ToArray();
    internal void Snap() { Value = Target.ToArray(); Array.Clear(Velocity); }
    internal bool Settled => Value.Zip(Target).All(x => Math.Abs(x.First - x.Second) < .015)
        && Velocity.All(x => Math.Abs(x) < .08);
    internal void Step(double seconds)
    {
        const double omega = 17;
        var decay = Math.Exp(-omega * seconds);
        for (var i = 0; i < Value.Length; i++)
        {
            var offset = Value[i] - Target[i]; var j = Velocity[i] + omega * offset;
            Value[i] = Target[i] + (offset + j * seconds) * decay;
            Velocity[i] = (Velocity[i] - omega * j * seconds) * decay;
        }
    }
}
