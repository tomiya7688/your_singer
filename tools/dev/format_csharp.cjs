// { 責務: [指定されたC#ファイルだけを固定SDKのdotnet format whitespaceで整形する] }
const fs = require("node:fs");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const root = fs.realpathSync(path.resolve(__dirname, "../.."));
const sdkVersion = "10.0.401";
const check = process.argv.includes("--check");
const inputs = process.argv.slice(2).filter((item) => item !== "--check");
if (inputs.length === 0)
  throw new Error("整形対象のC#ファイルを指定してください。");
const sdks = spawnSync("dotnet", ["--list-sdks"], { encoding: "utf8" });
if (sdks.status !== 0) throw new Error(".NET SDKを確認できません。");
const matches = sdks.stdout
  .split(/\r?\n/)
  .map((line) => line.match(/^10\.0\.401 \[(.+)\]$/))
  .filter(Boolean);
if (matches.length !== 1) throw new Error(`固定SDK ${sdkVersion} が必要です。`);
const formatter = path.join(
  matches[0][1],
  sdkVersion,
  "DotnetTools",
  "dotnet-format",
  "dotnet-format.dll",
);
if (!fs.existsSync(formatter))
  throw new Error("固定版dotnet formatが見つかりません。");
const projects = new Map();
for (const input of inputs) {
  const requested = path.resolve(input);
  const file = fs.realpathSync(input);
  const samePath =
    process.platform === "win32"
      ? requested.toLowerCase() === file.toLowerCase()
      : requested === file;
  if (!samePath)
    throw new Error(`整形対象にシンボリックリンクは使えません: ${input}`);
  const relative = path.relative(root, file);
  if (
    relative === ".." ||
    relative.startsWith(`..${path.sep}`) ||
    path.isAbsolute(relative) ||
    path.extname(file) !== ".cs" ||
    relative
      .split(path.sep)
      .some((part) => ["bin", "obj", "artifacts", "vendor"].includes(part))
  ) {
    throw new Error(`整形対象外のパスです: ${input}`);
  }
  let directory = path.dirname(file);
  let project;
  while (directory === root || directory.startsWith(`${root}${path.sep}`)) {
    const candidates = fs
      .readdirSync(directory)
      .filter((name) => name.endsWith(".csproj"));
    if (candidates.length > 1)
      throw new Error(`プロジェクトを一意に特定できません: ${input}`);
    if (candidates.length === 1) {
      project = path.join(directory, candidates[0]);
      break;
    }
    if (directory === root) break;
    directory = path.dirname(directory);
  }
  if (!project) throw new Error(`C#プロジェクトがありません: ${input}`);
  if (!projects.has(project)) projects.set(project, []);
  projects.get(project).push(relative);
}
for (const [project, files] of projects) {
  // { 処理: [依存を復元して指定ファイルだけを整形する] }
  const restore = spawnSync("dotnet", ["restore", project, "--nologo"], {
    cwd: root,
    stdio: "inherit",
  });
  if (restore.status !== 0)
    throw new Error(`プロジェクトの復元に失敗しました: ${project}`);
  const args = [
    "exec",
    formatter,
    "whitespace",
    project,
    "--no-restore",
    "--include",
    ...files,
  ];
  if (check) args.push("--verify-no-changes");
  const result = spawnSync("dotnet", args, { cwd: root, stdio: "inherit" });
  if (result.status !== 0)
    throw new Error(`C#の整形または整形確認に失敗しました: ${project}`);
}
