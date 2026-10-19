import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import ReadinessSummary from '../src/ReadinessSummary';

describe('ReadinessSummary', () => {
  it('renders the server sentence verbatim without recomputing it', () => {
    const response = {
      monthsUntilReturn: 9,
      gapsFound: 1,
      requestsSent: 1,
      heldForReview: 1,
      notProcessed: 2,
      sentence: 'Return in 9 months. 1 records gap found, 1 routine request sent, 1 item held for internal review, 2 documents not processed.',
    };

    render(<ReadinessSummary summary={response} />);
    expect(screen.getByRole('status').textContent).toBe(response.sentence);
  });
});
