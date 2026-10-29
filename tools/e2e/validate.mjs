import { execFileSync } from 'node:child_process';
import { readFile, writeFile, appendFile, copyFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const here=path.dirname(fileURLToPath(import.meta.url));
const tap=execFileSync(process.execPath,['--test',path.join(here,'self-test.mjs')],{encoding:'utf8',windowsHide:true});
process.stdout.write(tap);
for(const f of ['run.mjs','page.mjs','cdp.mjs'])execFileSync(process.execPath,['--check',path.join(here,f)],{windowsHide:true});
if(process.argv[2]){
  const out=path.resolve(process.argv[2]),json=path.join(out,'report.json'),md=path.join(out,'report.md');
  const report=JSON.parse(await readFile(json,'utf8'));
  report.harnessValidation={at:new Date().toISOString(),passed:Number(tap.match(/# pass (\d+)/)?.[1]),failed:Number(tap.match(/# fail (\d+)/)?.[1]),syntaxChecks:['run.mjs','page.mjs','cdp.mjs'],uiValidation:report.summary.actions?'See action ledger':'No UI actions executed; cold/warm viewer behaviour untested'};
  await writeFile(json,JSON.stringify(report,null,2));
  await appendFile(md,`\n## Harness validation\n\n${report.harnessValidation.passed} protocol/probe tests passed; ${report.harnessValidation.failed} failed. Syntax checks passed for run.mjs, page.mjs and cdp.mjs. These checks do not validate the desktop UI.\n`);
  const scratch=String.raw`C:\Users\Leshe\AppData\Local\Temp\claude\C--Projects-Map\2a840339-9a92-4e96-9cb6-e8811e371f66\scratchpad`;
  await copyFile(md,path.join(scratch,'e2e-report.md'));
}
