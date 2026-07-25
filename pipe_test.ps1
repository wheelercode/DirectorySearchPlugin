$pipe = [IO.Pipes.NamedPipeClientStream]::new(
    ".",
    "Wheelercode.DirectorySearchPlugin.Index",
    [IO.Pipes.PipeDirection]::InOut
)

$pipe.Connect(1000)

$writer = [IO.StreamWriter]::new($pipe)
$reader = [IO.StreamReader]::new($pipe)
$writer.AutoFlush = $true

$request = @{
    Query = "zeta"
    MaximumResults = 50
} | ConvertTo-Json -Compress

$writer.WriteLine($request)
$reader.ReadLine()