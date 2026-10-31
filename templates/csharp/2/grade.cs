var scores = new[] { 72, 88, 95 };
foreach (var score in scores)
{
    if (score >= 90)
        Console.WriteLine("A");
    else if (score >= 80)
        Console.WriteLine("B");
    else
        Console.WriteLine("C");
}
