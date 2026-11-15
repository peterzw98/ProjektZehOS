using System;
using System.Collections.Generic;
abstract class Shape { public abstract int Area(); }
sealed class Square(int side) : Shape { public override int Area() => side * side; }
static class Program
{
    static int Main()
    {
        var list = new List<Shape> { new Square(3), new Square(4) };
        int sum = 0;
        foreach (var s in list) sum += s.Area();
        try { throw new InvalidOperationException("x"); } catch (InvalidOperationException) { sum += 1; }
        Console.WriteLine($"CSPROBE {sum}");
        return sum == 26 ? 0 : 1;
    }
}
