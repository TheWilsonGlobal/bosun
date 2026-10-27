.PHONY: dev build test lint format sim dev-backend

dev:
	pnpm dev

build:
	pnpm build

test:
	pnpm test

lint:
	pnpm lint

format:
	pnpm format

sim:
	pnpm sim

dev-backend:
	pnpm dev:backend
