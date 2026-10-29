import { of, throwError } from 'rxjs';
import { OAuthSetupComponent } from './oauth-setup.component';

describe('OAuth setup', () => {
  let api: any;
  let component: OAuthSetupComponent;
  beforeEach(() => {
    api = {
      getCurrentOAuth: jasmine.createSpy().and.returnValue(of({ authorized: true, ownTeamName: 'Remembered team' })),
      completeOAuthFlow: jasmine.createSpy().and.returnValue(of({ ownTeamName: 'My team' })),
      logoutOAuth: jasmine.createSpy().and.returnValue(of({ success: true }))
    };
    component = new OAuthSetupComponent(api, { instant: (key: string) => key } as any);
  });

  it('recognizes a remembered session instead of asking for another PIN', async () => {
    await component.ngOnInit();
    expect(component.step).toBe(3);
    expect(component.teamName).toBe('Remembered team');
    expect(component.checkingSession).toBeFalse();
  });

  it('trims the copied PIN and displays the authorized club', async () => {
    component.sessionId = 'session';
    component.verifier = ' 123456\n';
    await component.completeAuthorization();
    expect(api.completeOAuthFlow).toHaveBeenCalledWith('session', '123456');
    expect(component.step).toBe(3);
    expect(component.teamName).toBe('My team');
  });

  it('does not submit an empty PIN', async () => {
    component.verifier = '   ';
    await component.completeAuthorization();
    expect(api.completeOAuthFlow).not.toHaveBeenCalled();
  });

  it('shows the backend error and allows retrying when authorization fails', async () => {
    api.completeOAuthFlow.and.returnValue(throwError(() => ({ error: { error: 'PIN expired' }, message: 'HTTP error' })));
    component.step = 2;
    component.verifier = '123';
    await component.completeAuthorization();
    expect(component.step).toBe(2);
    expect(component.error).toContain('PIN expired');
    expect(component.loading).toBeFalse();
  });

  it('returns to sign-in after removing the stored session', async () => {
    await component.ngOnInit();
    await component.logout();
    expect(api.logoutOAuth).toHaveBeenCalled();
    expect(component.step).toBe(1);
    expect(component.teamName).toBe('');
  });
});
