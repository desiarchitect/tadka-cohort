# Setup Guide - Desi Architect Cohort

Get everything installed before Day 1. This should take 30-45 minutes. If something doesn't work, post in `#doubts` on Discord with a screenshot of the error.

**Local database password:** Compose and `appsettings.Development.json` default to `tadka` / `tadka_local`. That dummy is committed so you can clone and run with no `.env`. It is a throwaway local Postgres, not a secret. When a password is real it does not belong in git (User Secrets locally, environment variables in production). Do not copy `.env.example` into a committed `.env` with real credentials — `.env` is gitignored.

## Required Tools

### 1. Docker Desktop

We use Docker to run infrastructure locally. **Day 1 is PostgreSQL only.** Later weeks add more containers to the same compose file — you will not install Redis or Kafka yourself.

**Version:** Docker Desktop 4.30+ (Docker Engine 26+)

**Install:**
- Windows: https://docs.docker.com/desktop/install/windows-install/
- Mac: https://docs.docker.com/desktop/install/mac-install/
- Linux: https://docs.docker.com/desktop/install/linux-install/

**Verify:**
```bash
docker --version
# Expected: Docker version 26.x or higher

docker compose version
# Expected: Docker Compose version v2.x
```

**Important:** On Windows, enable WSL 2 backend (Docker Desktop → Settings → General → Use WSL 2 based engine). On Mac, allocate at least 4 GB RAM (Settings → Resources).

How to use Compose with this repo (commands, service vs container name, troubleshooting): [`docs/learn/docker.md`](docs/learn/docker.md).

### 2. .NET 10 SDK

The Tadka backend is built with .NET 10.

**Version:** .NET 10 SDK (latest preview or GA, depending on timing)

**Install:** https://dotnet.microsoft.com/download/dotnet/10.0

**Verify:**
```bash
dotnet --version
# Expected: 10.0.xxx

dotnet --list-sdks
# Should show 10.0.xxx
```

### 3. VS Code

Our primary editor. You can use Rider or Visual Studio if you prefer, but all demos will use VS Code.

**Version:** Latest stable

**Install:** https://code.visualstudio.com/download

**Required Extensions:**
| Extension | ID | Why |
|-----------|-----|-----|
| C# Dev Kit | `ms-dotnettools.csdevkit` | IntelliSense, debugging, solution explorer |
| GitHub Copilot | `github.copilot` | AI pair programming (active subscription needed) |
| GitHub Copilot Chat | `github.copilot-chat` | Chat + Agent Mode |
| Docker | `ms-azuretools.vscode-docker` | Manage containers |
| PostgreSQL | `ckolkman.vscode-postgres` | Query database |
| REST Client | `humao.rest-client` | Test API endpoints |
| Mermaid Preview | `bierner.markdown-mermaid` | Render architecture diagrams |

**Install all at once:**
```bash
code --install-extension ms-dotnettools.csdevkit
code --install-extension github.copilot
code --install-extension github.copilot-chat
code --install-extension ms-azuretools.vscode-docker
code --install-extension ckolkman.vscode-postgres
code --install-extension humao.rest-client
code --install-extension bierner.markdown-mermaid
```

### 4. Git

**Version:** 2.40+

**Install:** https://git-scm.com/downloads

**Verify:**
```bash
git --version
# Expected: git version 2.40.x or higher
```

**Configure (if not already):**
```bash
git config --global user.name "Your Name"
git config --global user.email "your@email.com"
```

### 5. GitHub Account

You need a GitHub account to:
- Fork the starter kit repository
- Use GitHub Copilot (requires active subscription)
- Submit assignments via pull requests

If you don't have Copilot yet: https://github.com/features/copilot (Individual plan works fine)

What `CLAUDE.md` and `.github/copilot-instructions.md` are for, and the full `.github/` / `.claude/` catalog (skills, agents, prompts): [`docs/learn/ai-context-files.md`](docs/learn/ai-context-files.md).

## Optional but Recommended

### Postman or Insomnia
For testing APIs with a GUI. The REST Client VS Code extension covers most needs, but some people prefer a standalone tool.

### TablePlus or DBeaver
For browsing PostgreSQL with a GUI. The VS Code PostgreSQL extension works, but a dedicated tool is nicer for complex queries.

## Verify Everything Works

Run these commands one by one. All should pass.

```bash
# 1. Docker is running
docker run --rm hello-world
# Should print "Hello from Docker!"

# 2. PostgreSQL starts via Docker
docker run --rm -d --name test-pg -e POSTGRES_PASSWORD=test -p 5432:5432 postgres:16
# Wait 5 seconds, then:
docker exec test-pg pg_isready
# Should print "accepting connections"
docker stop test-pg

# 3. .NET builds and runs
mkdir test-dotnet && cd test-dotnet
dotnet new webapi -n TestApi
cd TestApi
dotnet build
# Should say "Build succeeded"
cd ../.. && rm -rf test-dotnet

# 4. VS Code opens with extensions
code --list-extensions | grep -i copilot
# Should show github.copilot and github.copilot-chat

# 5. Git works
git --version
```

## Troubleshooting

**Docker Desktop won't start on Windows?**
→ Enable "Virtual Machine Platform" and "Windows Subsystem for Linux" in Windows Features. Restart.

**.NET SDK not found after installation?**
→ Close and reopen your terminal. On Mac, you may need to add it to PATH: `export PATH="$PATH:$HOME/.dotnet"`

**Copilot not working in VS Code?**
→ Sign in: Ctrl+Shift+P → "GitHub Copilot: Sign In". Make sure your subscription is active.

**Port 5432 already in use?**
→ You have another PostgreSQL instance running. Stop it: `docker stop <container>` or change the port mapping in docker-compose.yml.

## Still Stuck?

Post in `#doubts` on Discord. Include:
1. What you tried
2. The exact error message (screenshot preferred)
3. Your OS (Windows/Mac/Linux) and version

We'll help you sort it out before Day 1.
