# ChallengeApp

This project was generated using [Angular CLI](https://github.com/angular/angular-cli) version 19.2.27.

## Development server

To start a local development server, run:

```bash
npm ci
npm start
```

Once the server is running, open your browser and navigate to `http://localhost:4200/`. The application will automatically reload whenever you modify any of the source files.

Start the backend separately using the root README. The existing API URL is `http://localhost:5089`; the five implemented pages are Login, Products, Create Order, Orders, and Order Details. Seed credentials are listed in the original root README. Use Node.js 22.x for the Angular 19 compatibility range.

## Code scaffolding

Angular CLI includes powerful code scaffolding tools. To generate a new component, run:

```bash
ng generate component component-name
```

For a complete list of available schematics (such as `components`, `directives`, or `pipes`), run:

```bash
ng generate --help
```

## Building

To build the project run:

```bash
npm run build
```

This will compile your project and store the build artifacts in the `dist/` directory. By default, the production build optimizes your application for performance and speed.

## Running unit tests

To execute unit tests with the [Karma](https://karma-runner.github.io) test runner, use the following command:

```bash
npm test -- --watch=false --browsers=ChromeHeadless
```

## Running end-to-end tests

No `ng e2e` target is configured in this starter. Real browser-to-API checks are separate from the Karma tests; they are not implied by `npm test`. Chrome is required for the headless tests; set `CHROME_BIN` if auto-detection fails.

## Additional Resources

For more information on using the Angular CLI, including detailed command references, visit the [Angular CLI Overview and Command Reference](https://angular.dev/tools/cli) page.
