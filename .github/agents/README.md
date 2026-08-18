# Project Agent Specifications

This directory contains detailed specifications for specialized agents used in the Azeroth Platform project.

## Available Agents

### 1. Frontend Specialist  
**File:** `frontend-specialist.agent.md`  
**Focus:** React, Vite, TypeScript, UI/UX, Setup Wizards, Real-time UI  
**Use for:** Component development, wizard flows, real-time updates, state management, accessibility

### 2. QA Engineer  
**File:** `QA.agent.md`  
**Focus:** Test strategy, integration tests, API contract verification, regression coverage  
**Use for:** Writing tests after a feature is implemented, verifying implementations against specs, reporting bugs, establishing feedback loops between developer agents and test suites

### 3. Enterprise Integration Architect  
**File:** `enterprise-integration-architect.agent.md`  
**Focus:** Docker containerization, microservices patterns, API design, large-scale integration  
**Use for:** Designing integration architectures, evaluating integration strategies, Docker deployment strategy, enterprise design patterns

## Backend Work

Backend work should use the standard Copilot cloud agent environment with the repository setup steps in
`.github/workflows/copilot-setup-steps.yml`. That keeps the .NET toolchain deterministic instead of relying on a
separate backend-specific agent profile.

## How to Use These Agents

When working with GitHub Copilot CLI or spawning specialized agents, reference these specifications to ensure consistent, expert-level assistance.

### Example: Spawning a Frontend Specialist

```
I need help creating the setup wizard UI.
Please act as the Frontend Specialist defined in .github/agents/frontend-specialist.agent.md.

Context:
- Multi-step wizard (5 steps: name, database, ports, advanced, review)
- Use React Hook Form with Zod validation
- Store progress in localStorage
- Support back/next navigation with validation
```

### Example: Spawning the QA Engineer

```
The accounts API has been implemented. Please act as the QA Engineer defined in .github/agents/QA.agent.md.

Context:
- Feature documentation: backend/API_REFERENCE_ACCOUNTS.md
- Implementation: backend/AzerothPlatform.Api/Controllers/AccountsController.cs
- Verify against the spec and write integration tests
```

## Agent Coordination

For features requiring full-stack work and quality assurance, follow this order:

1. **Define backend contracts first** - Use the standard cloud agent with the repository setup workflow.
2. **Frontend Specialist** - Implement UI based on those contracts.
3. **QA Engineer** - Verify both layers against the spec, write tests, and report any gaps or bugs back to the developer agents.
4. **Iterate** - Developer agents fix reported issues, QA Engineer re-validates.

## Updating Agent Specs

As the project evolves, update these specifications to:
- Include new libraries or patterns adopted
- Document project-specific conventions
- Add lessons learned
- Reference implemented examples from the codebase

## Project Context

All agents should be aware of:
- **Project:** Azeroth Platform - Web-based management for AzerothCore servers
- **Stack:** React + Vite (Frontend) + ASP.NET Core 10 (Backend)
- **Architecture:** Docker-in-Docker pattern, managing AzerothCore container stacks
- **Key Integrations:** Docker.DotNet, SignalR, SOAP interface to AzerothCore
- **Documentation:** See `ARCHITECTURE_ANALYSIS.md` for complete technical details
