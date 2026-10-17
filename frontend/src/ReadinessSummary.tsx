export type ReadinessSummaryData = {
  monthsUntilReturn: number;
  gapsFound: number;
  requestsSent: number;
  heldForReview: number;
  notProcessed: number;
  sentence: string;
};

export default function ReadinessSummary({ summary }: { summary: ReadinessSummaryData }) {
  return <p className="readiness-summary" role="status">{summary.sentence}</p>;
}
