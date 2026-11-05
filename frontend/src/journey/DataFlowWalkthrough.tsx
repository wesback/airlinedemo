import { useState } from 'react';
import './dataFlowWalkthrough.css';

type Flow = 'sources' | 'pdf';

const sourceSystems = ['technical', 'maintenance', 'finance', 'records', 'contracts'];

const sourceStages = [
  'Check the signed-in user’s authorization and case scope.',
  'Read permitted records from simulated fixture adapters.',
  'Pin permitted evidence references to the case evidence basis.',
  'Record an audit receipt and updated case revision.',
];

const pdfStages = [
  'Upload a PDF records package for the case.',
  'The server validates and parses the documents.',
  'Accepted documents are pinned as evidence.',
  'Only registered synthetic document text is sent to the records agent for investigation.',
  'Create a required human technical-review work item whether the records agent returns findings or not.',
];

export default function DataFlowWalkthrough() {
  const [flow, setFlow] = useState<Flow>('sources');

  return (
    <main className="av-page av-data-flows" id="main">
      <header className="av-data-flows-head">
        <p className="av-data-flows-kicker">Illustrative walkthrough · not live system status</p>
        <h1>Data flows</h1>
        <p className="av-lede">See the two separate ways evidence enters a case.</p>
      </header>

      <nav className="av-data-flow-controls" aria-label="Evidence flow">
        <button type="button" aria-pressed={flow === 'sources'} onClick={() => setFlow('sources')}>
          Simulated source retrieval
        </button>
        <button type="button" aria-pressed={flow === 'pdf'} onClick={() => setFlow('pdf')}>
          Uploaded PDF intake
        </button>
      </nav>

      {flow === 'sources' ? (
        <section className="av-data-flow-panel" aria-labelledby="source-flow-heading">
          <h2 id="source-flow-heading">Simulated source retrieval</h2>
          <p>Case-scoped authorization gates access to permitted source records. The adapters read local fixtures, pin permitted evidence references, and record an audit receipt.</p>
          <h3>Source systems</h3>
          <ol className="av-data-flow-sources" aria-label="Source systems">
            {sourceSystems.map((source) => <li key={source}>{source}</li>)}
          </ol>
          <h3>Retrieval stages</h3>
          <ol className="av-data-flow-stages" aria-label="Source retrieval stages">
            {sourceStages.map((stage) => <li key={stage}>{stage}</li>)}
          </ol>
        </section>
      ) : (
        <section className="av-data-flow-panel" aria-labelledby="pdf-flow-heading">
          <h2 id="pdf-flow-heading">Uploaded PDF intake</h2>
          <p>The server validates and parses an uploaded package, then pins accepted documents as evidence. Only registered synthetic document text is sent to the records agent; other PDFs go straight to human review.</p>
          <ol className="av-data-flow-stages" aria-label="PDF intake stages">
            {pdfStages.map((stage) => <li key={stage}>{stage}</li>)}
          </ol>
        </section>
      )}
    </main>
  );
}
