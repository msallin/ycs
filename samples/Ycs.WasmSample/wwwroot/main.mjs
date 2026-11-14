// Runs the program in Node: node main.mjs (from the published wwwroot folder).
import { dotnet } from './_framework/dotnet.js';

process.exitCode = await dotnet.run();
