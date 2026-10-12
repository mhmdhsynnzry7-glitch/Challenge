import { HttpErrorResponse } from '@angular/common/http';

export function apiErrorMessage(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) return 'Request failed. Please try again.';
  if (error.status === 0) return 'Cannot reach the API. Please check that it is running and try again.';
  if (error.status === 403) return 'You do not have permission to perform this action.';
  const body = error.error as { message?: string; title?: string; errors?: Record<string, string[]> } | null;
  const validation = body?.errors && Object.values(body.errors).flat().filter(value => typeof value === 'string');
  if (validation?.length) return validation.join(' ');
  if (typeof body?.message === 'string') return body.message;
  if (typeof body?.title === 'string') return body.title;
  if (error.status === 401) return 'Your session has expired. Please log in again.';
  return 'Request failed. Please try again.';
}
