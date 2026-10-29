import { of, Subject, throwError } from 'rxjs';
import { PlayersComponent } from './players.component';
import { DataCacheService } from '../../services/data-cache.service';
import { Team } from '../../models/team.model';

describe('Players data refresh', () => {
  let cache: DataCacheService;
  let api: any;
  let component: PlayersComponent;
  const team = (ids: number[]): Team => ({ teamId: 1, teamName: 'Team', players: ids.map(playerId => ({ playerId } as any)) });
  beforeEach(() => {
    cache = new DataCacheService();
    cache.auth$.next({ authorized: true, ownTeamId: 1 });
    api = { getPlayers: jasmine.createSpy().and.returnValue(of([])) };
    component = new PlayersComponent(api, { instant: (key: string) => key } as any, cache,
      {} as any, { recent: () => of([]), changed$: new Subject<void>() } as any);
  });
  afterEach(() => component.ngOnDestroy());

  it('updates an already cached roster and accepts an empty refreshed roster', () => {
    cache.ownTeam$.next(team([1]));
    component.ngOnInit();
    cache.ownTeam$.next(team([2, 3]));
    expect(component.players.map(p => p.playerId)).toEqual([2, 3]);
    cache.ownTeam$.next(team([]));
    expect(component.players).toEqual([]);
    expect(api.getPlayers).not.toHaveBeenCalled();
  });

  it('loads players on direct navigation and exits loading on an API failure', () => {
    api.getPlayers.and.returnValue(throwError(() => ({ message: 'CHPP unavailable' })));
    component.ngOnInit();
    expect(api.getPlayers).toHaveBeenCalledWith(1);
    expect(component.loading).toBeFalse();
    expect(component.error).toContain('CHPP unavailable');
  });
});
