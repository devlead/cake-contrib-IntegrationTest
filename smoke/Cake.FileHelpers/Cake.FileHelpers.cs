var filePath = context.FileSystem.GetFile("./probe.txt").Path;
context.FileWriteText(filePath, "ok");
var content = context.FileReadText(filePath);
if (content != "ok")
{
    throw new Exception($"Expected ok, got '{content}'");
}
