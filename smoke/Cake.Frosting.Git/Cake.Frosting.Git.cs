context.GitInit(".");
var readme = context.FileSystem.GetFile("./README.md");
readme.WriteAllText("probe");
context.GitAdd(".", readme.Path);
context.GitCommit(".", "Integration Test", "integration@test.local", "init");
context.GitTag(".", "v0.0.0");
var result = context.GitDescribe(".");
if (string.IsNullOrWhiteSpace(result))
{
    throw new Exception("GitDescribe returned empty");
}
